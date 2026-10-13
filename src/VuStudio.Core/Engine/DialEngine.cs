using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Config;
using VuStudio.Core.Faces;
using VuStudio.Core.History;
using VuStudio.Core.Lighting;
using VuStudio.Core.Mapping;
using VuStudio.Core.Profiles;
using VuStudio.Core.Sensors;

namespace VuStudio.Core.Engine;

/// <summary>
/// The heart of VU Studio. Every 50 ms it picks the active profile and, for each dial slot, the
/// current page; it then turns sensor values into needle positions, backlight levels and (rate
/// limited) e-paper faces, and hands them to <see cref="IDialOutput"/>. All mutable state lives on
/// the loop; public methods only enqueue commands or read immutable snapshots.
/// </summary>
public sealed class DialEngine : IAsyncDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan ProfileInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FaceInterval = TimeSpan.FromSeconds(1);

    private readonly ConfigStore _config;
    private readonly SensorHub _hub;
    private readonly IDialOutput _output;
    private readonly IFaceRenderer _renderer;
    private readonly HistoryRecorder? _history;
    private readonly INotificationSink? _notifications;
    private readonly ILogger<DialEngine> _logger;
    private readonly TimeProvider _time;
    private readonly ProfileSelector _selector = new();
    private readonly ProcessWatcher _processes;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Dictionary<int, SlotRuntime> _slots = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _revision = -1;
    private Profile? _activeProfile;
    private string? _manualProfileId;
    private NightAction _night;
    private bool _paused;
    private bool _suspended;
    private DateTimeOffset _lastProfileEval = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHistorySample = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSlotSync = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTick;
    private volatile EngineSnapshot _snapshot = EngineSnapshot.Empty;
    private long _snapshotVersion;

    public DialEngine(
        ConfigStore config,
        SensorHub hub,
        IDialOutput output,
        IFaceRenderer renderer,
        ILogger<DialEngine> logger,
        HistoryRecorder? history = null,
        INotificationSink? notifications = null,
        TimeProvider? time = null)
    {
        _config = config;
        _hub = hub;
        _output = output;
        _renderer = renderer;
        _logger = logger;
        _history = history;
        _notifications = notifications;
        _time = time ?? TimeProvider.System;
        _processes = new ProcessWatcher(_time);
    }

    public EngineSnapshot Snapshot => _snapshot;

    /// <summary>Incremented whenever <see cref="Snapshot"/> is replaced.</summary>
    public long SnapshotVersion => Interlocked.Read(ref _snapshotVersion);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastTick = _time.GetUtcNow();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null || _loop is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        if (_config.Current.Night.SleepOnShutdown)
        {
            ParkAll();
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Forces a profile (null returns to automatic selection).</summary>
    public void SetManualProfile(string? profileId) => _commands.Enqueue(() =>
    {
        _manualProfileId = profileId;
        _lastProfileEval = DateTimeOffset.MinValue;
    });

    /// <summary>Parks needles and switches lights off until resumed.</summary>
    public void SetPaused(bool paused) => _commands.Enqueue(() =>
    {
        _paused = paused;
        _lastProfileEval = DateTimeOffset.MinValue;
    });

    /// <summary>System suspend/resume notification.</summary>
    public void SetSuspended(bool suspended)
    {
        _commands.Enqueue(() =>
        {
            _suspended = suspended;
            _lastProfileEval = DateTimeOffset.MinValue;
            if (!suspended)
            {
                _output.Invalidate();
            }
        });

        if (suspended && _config.Current.Night.SleepOnShutdown)
        {
            ParkAll();
        }
    }

    /// <summary>Moves a slot to the next (+1) or previous (-1) page.</summary>
    public void Advance(int slot, int delta) => _commands.Enqueue(() =>
    {
        if (_slots.TryGetValue(slot, out var runtime) && runtime.PageCount > 0)
        {
            runtime.PageIndex = ((runtime.PageIndex + delta) % runtime.PageCount + runtime.PageCount) % runtime.PageCount;
            runtime.PageStarted = _time.GetUtcNow();
            runtime.ResetPage();
        }
    });

    /// <summary>Re-renders every face and re-sends all outputs.</summary>
    public void Refresh() => _commands.Enqueue(() =>
    {
        foreach (var runtime in _slots.Values)
        {
            runtime.FaceDirty = true;
        }

        _output.Invalidate();
    });

    /// <summary>The face last shown (or that would be shown) on a slot.</summary>
    public MonoBitmap? GetFace(int slot) => _slots.TryGetValue(slot, out var runtime) ? runtime.Face : null;

    /// <summary>
    /// Builds a render context for <paramref name="page"/> as if it were shown on <paramref name="slot"/> now.
    /// Used by editor previews; safe to call from any thread.
    /// </summary>
    public FaceRenderContext CreateContext(int slot, DialPage page)
    {
        var cfg = _config.Current;
        var sensor = page.Value.SensorId is { } id ? _hub.Get(id) : null;
        var resolved = BindingResolver.Resolve(page.Value, sensor?.Info, cfg.General);
        var (display, text) = ReadValue(sensor, resolved);
        return BuildContext(cfg, slot, page, sensor, resolved, display, text);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Engine tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One engine step. Public for deterministic tests.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        var elapsedMs = Math.Clamp((now - _lastTick).TotalMilliseconds, 0, 1000);
        _lastTick = now;

        while (_commands.TryDequeue(out var command))
        {
            command();
        }

        var cfg = _config.Current;
        if (cfg.Revision != _revision)
        {
            OnConfigChanged(cfg);
        }

        if (now - _lastSlotSync > TimeSpan.FromSeconds(2))
        {
            _lastSlotSync = now;
            SyncSlots(cfg);
            cfg = _config.Current;
        }

        if (now - _lastProfileEval >= ProfileInterval)
        {
            _lastProfileEval = now;
            _activeProfile = _selector.Select(cfg.Profiles, _manualProfileId, new Facts(this, now));
            _night = EvaluateNight(cfg, now);
        }

        var defaultProfile = cfg.Profiles.FirstOrDefault(p => p.Enabled && p.Activation == ProfileActivation.Always) ?? cfg.Profiles.FirstOrDefault();
        var slots = cfg.Hardware.Dials.Select(d => d.Slot)
            .Concat(_activeProfile?.Dials.Select(d => d.Slot) ?? [])
            .Concat(defaultProfile?.Dials.Select(d => d.Slot) ?? [])
            .Where(s => s > 0)
            .Distinct()
            .Order()
            .ToList();

        var states = new List<DialStateSnapshot>(slots.Count);
        foreach (var slot in slots)
        {
            if (!_slots.TryGetValue(slot, out var runtime))
            {
                runtime = new SlotRuntime(slot);
                _slots[slot] = runtime;
            }

            states.Add(StepSlot(cfg, runtime, defaultProfile, now, elapsedMs));
        }

        if (_history is not null && now - _lastHistorySample >= TimeSpan.FromSeconds(1))
        {
            _lastHistorySample = now;
            _history.Sample();
        }

        _snapshot = new EngineSnapshot(
            _activeProfile?.Id,
            _activeProfile?.Name,
            _manualProfileId,
            _night,
            _paused,
            states,
            now);
        Interlocked.Increment(ref _snapshotVersion);
    }

    private DialStateSnapshot StepSlot(AppConfig cfg, SlotRuntime rt, Profile? defaultProfile, DateTimeOffset now, double elapsedMs)
    {
        var dialSettings = cfg.Hardware.Dials.FirstOrDefault(d => d.Slot == rt.Slot);
        var uid = dialSettings?.Uid;
        var online = uid is not null && _output.Dials.Any(d => d.Uid == uid && d.Online);

        var assignment = _activeProfile?.Dials.FirstOrDefault(d => d.Slot == rt.Slot);
        var owner = _activeProfile;
        if ((assignment is null || assignment.Pages.Count == 0) && (_activeProfile?.InheritUnassigned ?? true) && defaultProfile is not null)
        {
            assignment = defaultProfile.Dials.FirstOrDefault(d => d.Slot == rt.Slot);
            owner = defaultProfile;
        }

        if (assignment is null || assignment.Pages.Count == 0)
        {
            return StepIdle(cfg, rt, uid, dialSettings?.Name, online, now);
        }

        var assignmentKey = $"{owner?.Id}:{rt.Slot}";
        if (assignmentKey != rt.AssignmentKey)
        {
            rt.AssignmentKey = assignmentKey;
            rt.PageIndex = 0;
            rt.PageStarted = now;
            rt.ResetPage();
        }

        rt.PageCount = assignment.Pages.Count;
        if (rt.PageIndex >= rt.PageCount)
        {
            rt.PageIndex = 0;
        }

        var current = assignment.Pages[rt.PageIndex];
        var duration = current.DurationSeconds ?? assignment.RotationSeconds;
        if (rt.PageCount > 1 && duration > 0 && (now - rt.PageStarted).TotalSeconds >= duration && !IsSleeping)
        {
            rt.PageIndex = (rt.PageIndex + 1) % rt.PageCount;
            rt.PageStarted = now;
            rt.ResetPage();
            current = assignment.Pages[rt.PageIndex];
        }

        var pageKey = $"{assignmentKey}:{current.Id}:{_revision}";
        if (pageKey != rt.PageKey)
        {
            rt.PageKey = pageKey;
            rt.FaceDirty = true;
        }

        var binding = current.Value;
        var sensor = binding.SensorId is { } sensorId ? _hub.Get(sensorId) : null;
        var resolved = BindingResolver.Resolve(binding, sensor?.Info, cfg.General);
        var (display, text) = ReadValue(sensor, resolved);
        var offline = binding.SensorId is not null && display is null && text is null;

        double fraction;
        if (display is { } d)
        {
            fraction = resolved.Mapping.ToFraction(d);
            rt.LastFraction = fraction;
        }
        else
        {
            fraction = offline && binding.Offline == OfflineBehavior.Hold ? rt.LastFraction : 0;
        }

        rt.Smoother.AttackMs = binding.AttackMs;
        rt.Smoother.ReleaseMs = binding.ReleaseMs;
        rt.Smoother.HoldMs = binding.HoldMs;
        var target = IsSleeping ? 0 : fraction * 100;
        var needle = Math.Clamp(rt.Smoother.Update(target, elapsedMs), 0, 100);

        var nightFactor = NightFactor(cfg) * Math.Clamp(cfg.Hardware.MasterBrightness, 0, 100) / 100.0;
        if (offline && binding.Offline == OfflineBehavior.Dim)
        {
            nightFactor *= 0.3;
        }

        var inputs = new BacklightInputs(
            display,
            fraction,
            now,
            _hub.GetNumber,
            _hub.GetText,
            ParseColor(_hub.GetText(WellKnownSensors.AccentColor)),
            ParseColor(_hub.GetText(WellKnownSensors.AlbumColor)),
            nightFactor);
        var light = BacklightEngine.Compute(current.Light, rt.LightState, inputs);

        if (uid is not null)
        {
            _output.SetNeedle(uid, needle);
            _output.SetBacklight(uid, light.Levels);
            _output.SetBacklightTransition(uid, light.TransitionMs);
            var easing = binding.NeedleEasing ?? dialSettings?.NeedleEasing ?? Easing.DefaultNeedle;
            if (easing != rt.NeedleEasing)
            {
                rt.NeedleEasing = easing;
                _output.SetNeedleEasing(uid, easing);
            }
        }

        FaceRenderContext? context = null;
        if (light.RaisedAlarm is { } alarm && _notifications is not null)
        {
            context ??= BuildContext(cfg, rt.Slot, current, sensor, resolved, display, text);
            var message = TextTemplate.Render(alarm.Message ?? "{title}: {value}{unit}", context);
            _notifications.Notify(new EngineNotification(context.Title, message, context.Icon, rt.Slot));
        }

        if (rt.FaceDirty || now - rt.LastFaceRender >= FaceInterval)
        {
            context ??= BuildContext(cfg, rt.Slot, current, sensor, resolved, display, text);
            UpdateFace(rt, uid, current.Face, context, now);
        }

        rt.Title = context?.Title ?? rt.Title;
        return new DialStateSnapshot(
            rt.Slot,
            uid,
            dialSettings?.Name,
            online,
            owner?.Id,
            rt.PageIndex,
            rt.PageCount,
            current.Id,
            current.Name,
            binding.SensorId,
            display,
            text,
            resolved.DisplayUnit,
            rt.Title,
            Math.Round(needle, 2),
            light.Levels,
            light.Levels.ToPreviewColor().ToHex(),
            rt.FaceVersion,
            true,
            light.ActiveAlarm is not null);
    }

    private DialStateSnapshot StepIdle(AppConfig cfg, SlotRuntime rt, string? uid, string? name, bool online, DateTimeOffset now)
    {
        if (rt.AssignmentKey != "idle")
        {
            rt.AssignmentKey = "idle";
            rt.PageKey = null;
            rt.PageCount = 0;
            rt.PageIndex = 0;
            rt.ResetPage();
        }

        var needle = rt.Smoother.Update(0, 50);
        if (uid is not null)
        {
            _output.SetNeedle(uid, needle);
            _output.SetBacklight(uid, LedLevels.Off);
        }

        if (rt.FaceDirty || rt.Face is null)
        {
            var title = name ?? $"Dial {rt.Slot}";
            var context = new FaceRenderContext
            {
                Title = title,
                Subtitle = "VU Studio",
                Icon = "gauge",
                Geometry = cfg.Hardware.Geometry,
                Now = now.ToLocalTime(),
                Culture = CultureFor(cfg),
            };
            UpdateFace(rt, uid, new FaceConfig { Theme = "minimal/arc", ShowValue = false, WarnFrom = null, MinRefreshSeconds = 0 }, context, now);
            rt.Title = title;
        }

        return new DialStateSnapshot(rt.Slot, uid, name, online, null, 0, 0, null, null, null, null, null, "", rt.Title,
            Math.Round(needle, 2), LedLevels.Off, "#000000", rt.FaceVersion, false, false);
    }

    private void UpdateFace(SlotRuntime rt, string? uid, FaceConfig face, FaceRenderContext context, DateTimeOffset now)
    {
        rt.LastFaceRender = now;
        MonoBitmap bitmap;
        try
        {
            var document = _renderer.BuildDocument(face, context);
            bitmap = _renderer.Render(document, context);
        }
        catch (Exception ex)
        {
            if (rt.FaceDirty)
            {
                _logger.LogWarning(ex, "Rendering the face of slot {Slot} failed", rt.Slot);
            }

            rt.FaceDirty = false;
            return;
        }

        var hash = bitmap.ComputeHash();
        if (hash != rt.FaceHash)
        {
            var due = rt.FaceDirty || rt.Face is null || (now - rt.LastFaceSent).TotalSeconds >= Math.Max(5, face.MinRefreshSeconds);
            if (due)
            {
                rt.FaceHash = hash;
                rt.Face = bitmap;
                rt.LastFaceSent = now;
                rt.FaceVersion++;
                if (uid is not null)
                {
                    _output.SetFace(uid, bitmap);
                }
            }
        }
        else if (rt.FaceDirty && uid is not null && rt.Face is not null && rt.FaceUid != uid)
        {
            _output.SetFace(uid, rt.Face);
        }

        rt.FaceUid = uid;
        rt.FaceDirty = false;
    }

    private FaceRenderContext BuildContext(AppConfig cfg, int slot, DialPage page, SensorSnapshot? sensor, ResolvedBinding resolved, double? display, string? text)
    {
        var info = sensor?.Info;
        var title = FirstNonEmpty(page.Face.Title, page.Value.Label, info?.Name, page.Name) ?? $"Dial {slot}";
        return new FaceRenderContext
        {
            Title = title,
            Subtitle = page.Face.Subtitle ?? "",
            Icon = page.Face.Icon ?? info?.Icon ?? CategoryIcon(info?.Category),
            Mapping = resolved.Mapping,
            Value = display,
            ValueText = text,
            Unit = resolved.DisplayUnit,
            Decimals = resolved.Decimals,
            SensorId = page.Value.SensorId,
            Geometry = cfg.Hardware.Geometry,
            Now = _time.GetUtcNow().ToLocalTime(),
            Culture = CultureFor(cfg),
            Sensors = _hub.Get,
            History = (id, window) => _history?.GetRecent(id, window) ?? [],
        };
    }

    private static (double? Display, string? Text) ReadValue(SensorSnapshot? sensor, ResolvedBinding resolved)
    {
        if (sensor is null || sensor.IsStale)
        {
            return (null, null);
        }

        if (sensor.Info.ValueKind != SensorValueKind.Number)
        {
            return (sensor.Value is { } v ? resolved.ToDisplay(v) : null, sensor.Text);
        }

        return (sensor.Value is { } n ? resolved.ToDisplay(n) : null, null);
    }

    private bool IsSleeping => _night == NightAction.Sleep;

    private double NightFactor(AppConfig cfg) => _night switch
    {
        NightAction.Dim => Math.Clamp(cfg.Night.DimPercent, 0, 100) / 100.0,
        NightAction.LightsOff or NightAction.Sleep => 0,
        _ => 1,
    };

    private NightAction EvaluateNight(AppConfig cfg, DateTimeOffset now)
    {
        if (_suspended || _paused)
        {
            return NightAction.Sleep;
        }

        var action = NightAction.None;
        if (_hub.GetNumber(WellKnownSensors.SessionLocked) is >= 0.5)
        {
            action = Max(action, cfg.Night.OnLock);
        }

        if (cfg.Night.IdleMinutes > 0 && _hub.GetNumber(WellKnownSensors.SessionIdleSeconds) is { } idle && idle >= cfg.Night.IdleMinutes * 60)
        {
            action = Max(action, cfg.Night.OnIdle);
        }

        if (cfg.Night.QuietHoursEnabled)
        {
            var quiet = new ScheduleCondition { From = cfg.Night.QuietFrom, To = cfg.Night.QuietTo };
            if (ProfileSelector.InSchedule(quiet, now.ToLocalTime().DateTime))
            {
                action = Max(action, cfg.Night.QuietAction);
            }
        }

        return action;

        static NightAction Max(NightAction a, NightAction b) => (NightAction)Math.Max((int)a, (int)b);
    }

    private void OnConfigChanged(AppConfig cfg)
    {
        _revision = cfg.Revision;
        _lastProfileEval = DateTimeOffset.MinValue;
        foreach (var runtime in _slots.Values)
        {
            runtime.FaceDirty = true;
            runtime.LightState.Reset();
        }

        if (_history is not null)
        {
            _history.RetentionDays = cfg.History.RetentionDays;
            _history.PersistenceEnabled = cfg.History.Enabled;
            _history.SetRecordedSensors(CollectRecordedSensors(cfg));
        }
    }

    /// <summary>Gives new dials the next free slot so they show up in the UI immediately.</summary>
    private void SyncSlots(AppConfig cfg)
    {
        var known = cfg.Hardware.Dials.Select(d => d.Uid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = _output.Dials.Where(d => !known.Contains(d.Uid)).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        _config.Update(c =>
        {
            foreach (var dial in fresh.OrderBy(d => d.BusIndex))
            {
                var slot = 1;
                while (c.Hardware.Dials.Any(d => d.Slot == slot))
                {
                    slot++;
                }

                c.Hardware.Dials.Add(new DialSettings { Uid = dial.Uid, Slot = slot });
                _logger.LogInformation("New dial {Uid} assigned to slot {Slot}", dial.Uid, slot);
            }
        });
    }

    private void ParkAll()
    {
        foreach (var dial in _config.Current.Hardware.Dials)
        {
            _output.SetNeedle(dial.Uid, 0);
            _output.SetBacklight(dial.Uid, LedLevels.Off);
        }
    }

    private static IEnumerable<string> CollectRecordedSensors(AppConfig cfg)
    {
        foreach (var page in cfg.Profiles.SelectMany(p => p.Dials).SelectMany(d => d.Pages))
        {
            if (page.Value.SensorId is { } id)
            {
                yield return id;
            }

            foreach (var alarm in page.Light.Alarms.Where(a => a.SensorId is not null))
            {
                yield return alarm.SensorId!;
            }

            foreach (var layer in page.Face.Custom?.Layers ?? [])
            {
                if (layer is SparklineLayer { SensorId: { } s })
                {
                    yield return s;
                }
            }
        }

        foreach (var extra in cfg.History.ExtraSensors)
        {
            yield return extra;
        }
    }

    private static CultureInfo CultureFor(AppConfig cfg) => cfg.General.Language switch
    {
        "it" => CultureInfo.GetCultureInfo("it-IT"),
        "en" => CultureInfo.GetCultureInfo("en-US"),
        _ => CultureInfo.CurrentUICulture.Name.StartsWith("it", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("it-IT")
            : CultureInfo.GetCultureInfo("en-US"),
    };

    private static RgbwColor? ParseColor(string? text) => RgbwColor.TryParse(text, out var c) ? c : null;

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public static string CategoryIcon(SensorCategory? category) => category switch
    {
        SensorCategory.Cpu => "cpu-64-bit",
        SensorCategory.Gpu => "expansion-card",
        SensorCategory.Memory => "memory",
        SensorCategory.Storage => "harddisk",
        SensorCategory.Network => "lan",
        SensorCategory.Cooling => "fan",
        SensorCategory.Power => "flash",
        SensorCategory.Battery => "battery",
        SensorCategory.Motherboard => "chip",
        SensorCategory.Audio => "volume-high",
        SensorCategory.Media => "music",
        SensorCategory.Game => "gamepad-variant",
        SensorCategory.Process => "console",
        SensorCategory.Time => "clock-outline",
        SensorCategory.Weather => "weather-partly-cloudy",
        SensorCategory.Finance => "chart-line",
        SensorCategory.Home => "home",
        SensorCategory.System => "monitor-dashboard",
        _ => "gauge",
    };

    private sealed class Facts(DialEngine engine, DateTimeOffset now) : IProfileFacts
    {
        public IReadOnlySet<string> RunningProcesses => engine._processes.Names;

        public string? ForegroundProcess => engine._hub.GetText(WellKnownSensors.ForegroundProcess)?.ToLowerInvariant();

        public DateTime LocalNow { get; } = now.ToLocalTime().DateTime;

        public double? Number(string sensorId) => engine._hub.GetNumber(sensorId);
    }

    private sealed class SlotRuntime(int slot)
    {
        public int Slot { get; } = slot;

        public string? AssignmentKey { get; set; }

        public string? PageKey { get; set; }

        public int PageIndex { get; set; }

        public int PageCount { get; set; }

        public DateTimeOffset PageStarted { get; set; }

        public Smoother Smoother { get; } = new();

        public BacklightState LightState { get; } = new();

        public double LastFraction { get; set; }

        public Easing? NeedleEasing { get; set; }

        public MonoBitmap? Face { get; set; }

        public string? FaceUid { get; set; }

        public ulong FaceHash { get; set; }

        public long FaceVersion { get; set; }

        public bool FaceDirty { get; set; } = true;

        public DateTimeOffset LastFaceRender { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset LastFaceSent { get; set; } = DateTimeOffset.MinValue;

        public string Title { get; set; } = "";

        public void ResetPage()
        {
            LightState.Reset();
            FaceDirty = true;
            PageKey = null;
        }
    }
}
