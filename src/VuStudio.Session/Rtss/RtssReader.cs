using System.Buffers.Binary;
using System.Text;

namespace VuStudio.Session.Rtss;

/// <summary>One application entry of the RTSS shared memory.</summary>
public sealed record RtssApp(
    int Slot,
    uint ProcessId,
    string Name,
    uint Flags,
    uint Time0,
    uint Time1,
    uint Frames,
    uint FrameTimeMicroseconds)
{
    /// <summary>
    /// Frames per second: the once-per-second period value (<c>1000 · frames / (time1 − time0)</c>),
    /// else the instantaneous value from the last frame time, else <c>null</c>.
    /// </summary>
    public double? Fps
    {
        get
        {
            var period = unchecked(Time1 - Time0);
            if (Time0 != 0 && period is > 0 and <= 10_000 && Frames > 0)
            {
                return Math.Min(1000.0 * Frames / period, RtssReader.MaxFps);
            }

            return FrameTimeMicroseconds > 0 ? Math.Min(1_000_000.0 / FrameTimeMicroseconds, RtssReader.MaxFps) : null;
        }
    }

    /// <summary>Frame time in ms: the last frame's (reacts per frame), else <c>1000 / fps</c>.</summary>
    public double? FrameTimeMs =>
        FrameTimeMicroseconds > 0 ? FrameTimeMicroseconds / 1000.0 : Fps is > 0 and var fps ? 1000.0 / fps : null;

    /// <summary>True when the entry carries any measurement.</summary>
    public bool HasData => Frames > 0 || FrameTimeMicroseconds > 0;
}

/// <summary>Parsed header and application entries.</summary>
public sealed record RtssSnapshot(uint Version, IReadOnlyList<RtssApp> Apps, int? ForegroundSlot, uint? ForegroundProcessId)
{
    /// <summary>The entry RTSS reports as the last foreground 3D application, when valid.</summary>
    public RtssApp? Foreground =>
        ForegroundSlot is { } slot && ForegroundProcessId is { } pid && pid != 0
            ? Apps.FirstOrDefault(a => a.Slot == slot && a.ProcessId == pid)
            : null;
}

/// <summary>Frame statistics of the selected application.</summary>
public sealed record RtssFrameStats(uint ProcessId, string App, double Fps, double FrameTimeMs);

/// <summary>
/// Pure parser of the <c>RTSSSharedMemoryV2</c> mapping (docs/integrations/windows-sources.md §2). Offsets are
/// the SDK's; fields are read only when <c>dwAppEntrySize</c> covers them and <c>dwVersion</c> is recent enough.
/// Never throws on malformed or truncated input: it returns <c>null</c>.
/// </summary>
public static class RtssReader
{
    /// <summary><c>'RTSS'</c> as a little-endian uint32 (the bytes read "SSTR").</summary>
    public const uint Signature = 0x52545353;

    /// <summary>Written while the mapping is being freed.</summary>
    public const uint DeadSignature = 0xDEAD;

    public const uint MinVersion = 0x00020000;

    /// <summary>v2.16: <c>dwLastForegroundApp</c> and <c>dwLastForegroundAppProcessID</c>.</summary>
    public const uint ForegroundVersion = 0x00020010;

    public const double MaxFps = 10_000;

    // Header offsets.
    internal const int HeaderSignature = 0;
    internal const int HeaderVersion = 4;
    internal const int HeaderAppEntrySize = 8;
    internal const int HeaderAppArrOffset = 12;
    internal const int HeaderAppArrSize = 16;
    internal const int HeaderLastForegroundApp = 64;
    internal const int HeaderLastForegroundAppProcessId = 68;
    internal const int HeaderSize = 72;

    // App entry offsets.
    internal const int EntryProcessId = 0;
    internal const int EntryName = 4;
    internal const int EntryNameLength = 260;
    internal const int EntryFlags = 264;
    internal const int EntryTime0 = 268;
    internal const int EntryTime1 = 272;
    internal const int EntryFrames = 276;
    internal const int EntryFrameTime = 280;
    internal const int EntryMinSize = 284;

    private const int MaxEntries = 4096;

    public static RtssSnapshot? Parse(ReadOnlySpan<byte> memory)
    {
        if (memory.Length < HeaderSize || U32(memory, HeaderSignature) != Signature)
        {
            return null;
        }

        var version = U32(memory, HeaderVersion);
        var entrySize = U32(memory, HeaderAppEntrySize);
        var arrOffset = U32(memory, HeaderAppArrOffset);
        var arrSize = U32(memory, HeaderAppArrSize);
        if (version < MinVersion || entrySize < EntryMinSize || arrOffset < HeaderSize || arrOffset >= memory.Length || arrSize > MaxEntries)
        {
            return null;
        }

        // Trust only what fits in the buffer.
        var fits = (memory.Length - (long)arrOffset) / entrySize;
        var count = (int)Math.Min(arrSize, fits);
        var apps = new List<RtssApp>();
        for (var i = 0; i < count; i++)
        {
            var entry = memory.Slice((int)(arrOffset + ((long)i * entrySize)), EntryMinSize);
            var pid = U32(entry, EntryProcessId);
            if (pid == 0)
            {
                continue;
            }

            apps.Add(new RtssApp(
                i,
                pid,
                ReadName(entry.Slice(EntryName, EntryNameLength)),
                U32(entry, EntryFlags),
                U32(entry, EntryTime0),
                U32(entry, EntryTime1),
                U32(entry, EntryFrames),
                U32(entry, EntryFrameTime)));
        }

        int? foregroundSlot = null;
        uint? foregroundPid = null;
        if (version >= ForegroundVersion)
        {
            foregroundSlot = (int)Math.Min(U32(memory, HeaderLastForegroundApp), int.MaxValue);
            foregroundPid = U32(memory, HeaderLastForegroundAppProcessId);
        }

        return new RtssSnapshot(version, apps, foregroundSlot, foregroundPid);
    }

    /// <summary>
    /// Stateless read: statistics of the foreground 3D application (<c>dwLastForegroundApp</c>), falling back
    /// to the most recently updated entry. Use <see cref="RtssTracker"/> to also drop stale entries.
    /// </summary>
    public static RtssFrameStats? Read(ReadOnlySpan<byte> memory) =>
        Parse(memory) is { } snapshot ? ToStats(Select(snapshot, snapshot.Apps.Where(a => a.HasData).ToList(), null)) : null;

    /// <summary>Foreground entry (RTSS's own, then the foreground window's PID), else the most recently updated one.</summary>
    internal static RtssApp? Select(RtssSnapshot snapshot, IReadOnlyList<RtssApp> candidates, uint? foregroundWindowPid)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (snapshot.Foreground is { } fg && candidates.Contains(fg))
        {
            return fg;
        }

        if (foregroundWindowPid is { } pid && candidates.FirstOrDefault(a => a.ProcessId == pid) is { } match)
        {
            return match;
        }

        var best = candidates[0];
        foreach (var app in candidates)
        {
            if (unchecked((int)(app.Time1 - best.Time1)) > 0)
            {
                best = app;
            }
        }

        return best;
    }

    internal static RtssFrameStats? ToStats(RtssApp? app) =>
        app is { Fps: { } fps } && fps > 0
            ? new RtssFrameStats(app.ProcessId, app.Name, Math.Round(fps, 1), Math.Round(app.FrameTimeMs ?? (1000.0 / fps), 2))
            : null;

    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));

    /// <summary>ANSI <c>char[MAX_PATH]</c>, often a full path: returns the last path segment.</summary>
    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        var text = Encoding.Latin1.GetString(end >= 0 ? raw[..end] : raw).Trim();
        var slash = text.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? text[(slash + 1)..] : text;
    }
}

/// <summary>
/// Stateful selection over successive snapshots: an entry counts as live only while its measurement period
/// (<c>dwTime0</c>/<c>dwTime1</c>/<c>dwFrames</c>) keeps changing; a minimised or paused game keeps its slot
/// but stops updating (§2.3). Launchers, overlays and browsers are ignored.
/// </summary>
public sealed class RtssTracker
{
    public static readonly IReadOnlySet<string> DefaultIgnored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "dwm.exe", "rtss.exe", "msiafterburner.exe", "steam.exe", "steamwebhelper.exe",
        "epicgameslauncher.exe", "epicwebhelper.exe", "galaxyclient.exe", "eadesktop.exe", "battle.net.exe", "ubisoftconnect.exe",
        "discord.exe", "nvidia share.exe", "nvcontainer.exe", "nvidia app.exe", "msedgewebview2.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "vustudio.exe",
    };

    private readonly Dictionary<(int Slot, uint Pid), Seen> _seen = new();
    private readonly TimeSpan _staleAfter;
    private readonly IReadOnlySet<string> _ignored;

    public RtssTracker(TimeSpan? staleAfter = null, IReadOnlySet<string>? ignored = null)
    {
        _staleAfter = staleAfter ?? TimeSpan.FromSeconds(2);
        _ignored = ignored ?? DefaultIgnored;
    }

    /// <summary>
    /// Feeds a snapshot taken at monotonic time <paramref name="now"/> and returns the statistics of the active
    /// application, or <c>null</c> (RTSS absent or no live 3D application).
    /// </summary>
    public RtssFrameStats? Update(RtssSnapshot? snapshot, TimeSpan now, uint? foregroundWindowPid = null)
    {
        if (snapshot is null)
        {
            _seen.Clear();
            return null;
        }

        var live = new List<RtssApp>();
        var present = new HashSet<(int, uint)>();
        foreach (var app in snapshot.Apps)
        {
            var key = (app.Slot, app.ProcessId);
            present.Add(key);
            var stamp = (app.Time0, app.Time1, app.Frames);
            if (!_seen.TryGetValue(key, out var seen))
            {
                // First sight: unknown freshness until the period changes (RTSS updates it about once a second).
                _seen[key] = new Seen(stamp, null);
                continue;
            }

            if (seen.Stamp != stamp)
            {
                seen = new Seen(stamp, now);
                _seen[key] = seen;
            }

            if (seen.ChangedAt is { } changed && now - changed <= _staleAfter && app.HasData && !_ignored.Contains(app.Name))
            {
                live.Add(app);
            }
        }

        foreach (var key in _seen.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _seen.Remove(key);
        }

        return RtssReader.ToStats(RtssReader.Select(snapshot, live, foregroundWindowPid));
    }

    private readonly record struct Seen((uint, uint, uint) Stamp, TimeSpan? ChangedAt);
}
