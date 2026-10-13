using System.Buffers.Binary;
using System.Text;
using VuStudio.Session.Rtss;

namespace VuStudio.Tests.Session;

public class RtssReaderTests
{
    private const int ArrOffset = 4096;
    private const int EntrySize = 12416; // v2.21 layout

    /// <summary>Builds a synthetic RTSSSharedMemoryV2 image from the documented offsets.</summary>
    private sealed class Builder(int slots = 4, uint version = 0x00020015, int entrySize = EntrySize)
    {
        public byte[] Bytes { get; } = Create(slots, version, entrySize);

        private static byte[] Create(int slots, uint version, int entrySize)
        {
            var b = new byte[ArrOffset + (slots * entrySize)];
            Put(b, 0, 0x52545353);
            Put(b, 4, version);
            Put(b, 8, (uint)entrySize);
            Put(b, 12, ArrOffset);
            Put(b, 16, (uint)slots);
            return b;
        }

        public Builder App(int slot, uint pid, string name, uint t0, uint t1, uint frames, uint frameTimeUs)
        {
            var e = ArrOffset + (slot * entrySize);
            Put(Bytes, e, pid);
            Encoding.Latin1.GetBytes(name).CopyTo(Bytes, e + 4);
            Put(Bytes, e + 264, 0x10007); // x64 | D3D11
            Put(Bytes, e + 268, t0);
            Put(Bytes, e + 272, t1);
            Put(Bytes, e + 276, frames);
            Put(Bytes, e + 280, frameTimeUs);
            return this;
        }

        public Builder Foreground(uint slot, uint pid)
        {
            Put(Bytes, 64, slot);
            Put(Bytes, 68, pid);
            return this;
        }

        public static void Put(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset, 4), value);
    }

    [Fact]
    public void Parses_entries_and_computes_fps_and_frame_time()
    {
        var mem = new Builder()
            .App(0, 1234, @"C:\Games\Elden Ring\Game\eldenring.exe", 10_000, 11_000, 60, 16_667)
            .Bytes;

        var snapshot = RtssReader.Parse(mem)!;
        Assert.Equal(0x00020015u, snapshot.Version);
        var app = Assert.Single(snapshot.Apps);
        Assert.Equal("eldenring.exe", app.Name);
        Assert.Equal(1234u, app.ProcessId);
        Assert.Equal(60, app.Fps!.Value, 6);
        Assert.Equal(16.667, app.FrameTimeMs!.Value, 3);

        var stats = RtssReader.Read(mem)!;
        Assert.Equal(new RtssFrameStats(1234, "eldenring.exe", 60, 16.67), stats);
    }

    [Fact]
    public void Prefers_the_last_foreground_application()
    {
        var mem = new Builder()
            .App(0, 100, "launcher.exe", 50_000, 51_000, 30, 33_333)
            .App(2, 200, "game.exe", 10_000, 11_000, 144, 6_944)
            .Foreground(2, 200)
            .Bytes;

        Assert.Equal("game.exe", RtssReader.Read(mem)!.App);
        Assert.Equal(144, RtssReader.Read(mem)!.Fps);
    }

    [Fact]
    public void Falls_back_to_the_most_recently_updated_entry()
    {
        // Foreground slot points at an entry whose PID does not match (slot reused): ignored.
        var mem = new Builder()
            .App(0, 100, "old.exe", 10_000, 11_000, 30, 0)
            .App(1, 200, "recent.exe", 20_000, 21_000, 90, 0)
            .Foreground(0, 999)
            .Bytes;

        var stats = RtssReader.Read(mem)!;
        Assert.Equal("recent.exe", stats.App);
        Assert.Equal(90, stats.Fps);
        Assert.Equal(11.11, stats.FrameTimeMs); // no per-frame sample: 1000 / fps
    }

    [Fact]
    public void Most_recent_comparison_survives_tick_wraparound()
    {
        var mem = new Builder(version: 0x0002000E) // before 2.16: no foreground fields
            .App(0, 1, "before-wrap.exe", 0xFFFF_F000, 0xFFFF_FC00, 60, 0)
            .App(1, 2, "after-wrap.exe", 0x0000_0100, 0x0000_0500, 60, 0)
            .Foreground(0, 1)
            .Bytes;

        Assert.Null(RtssReader.Parse(mem)!.ForegroundSlot);
        Assert.Equal("after-wrap.exe", RtssReader.Read(mem)!.App);
    }

    [Fact]
    public void Uses_the_instantaneous_rate_before_the_first_period()
    {
        var mem = new Builder().App(0, 1, "g.exe", 0, 0, 0, 8_000).Bytes;
        var stats = RtssReader.Read(mem)!;
        Assert.Equal(125, stats.Fps);
        Assert.Equal(8, stats.FrameTimeMs);
    }

    [Theory]
    [InlineData(0u)]          // empty / zeroed memory
    [InlineData(0xDEADu)]     // being freed
    [InlineData(0x12345678u)] // garbage
    public void Rejects_invalid_signatures(uint signature)
    {
        var mem = new Builder().App(0, 1, "g.exe", 1, 1001, 60, 0).Bytes;
        Builder.Put(mem, 0, signature);
        Assert.Null(RtssReader.Parse(mem));
        Assert.Null(RtssReader.Read(mem));
    }

    [Fact]
    public void Never_reads_past_a_truncated_or_inconsistent_buffer()
    {
        var full = new Builder(slots: 3).App(0, 1, "a.exe", 1, 1001, 60, 0).App(2, 3, "c.exe", 1, 1001, 30, 0).Bytes;

        // The header claims three slots but only the first fits.
        var truncated = full.AsSpan(0, ArrOffset + EntrySize + 100).ToArray();
        Assert.Equal(["a.exe"], RtssReader.Parse(truncated)!.Apps.Select(a => a.Name));

        Assert.Null(RtssReader.Parse(full.AsSpan(0, 40)));
        var badEntrySize = (byte[])full.Clone();
        Builder.Put(badEntrySize, 8, 100); // smaller than the fields we read
        Assert.Null(RtssReader.Parse(badEntrySize));
        var badOffset = (byte[])full.Clone();
        Builder.Put(badOffset, 12, (uint)full.Length + 10);
        Assert.Null(RtssReader.Parse(badOffset));
        var oldVersion = (byte[])full.Clone();
        Builder.Put(oldVersion, 4, 0x00010005);
        Assert.Null(RtssReader.Parse(oldVersion));
    }

    [Fact]
    public void No_data_means_no_stats()
    {
        Assert.Null(RtssReader.Read(new Builder().Bytes));
        Assert.Null(RtssReader.Read(new Builder().App(0, 5, "idle.exe", 0, 0, 0, 0).Bytes));
    }

    [Fact]
    public void Tracker_reports_only_live_entries_and_skips_ignored_ones()
    {
        var tracker = new RtssTracker(TimeSpan.FromSeconds(2));
        var t = TimeSpan.FromSeconds(100);

        RtssSnapshot Snap(uint gameT1, uint chromeT1) => RtssReader.Parse(new Builder()
            .App(0, 10, "chrome.exe", chromeT1 - 1000, chromeT1, 60, 16_667)
            .App(1, 20, "game.exe", gameT1 - 1000, gameT1, 100, 10_000)
            .Bytes)!;

        // First sight: freshness unknown.
        Assert.Null(tracker.Update(Snap(5_000, 5_000), t));

        // The game's period advanced: live. The browser advanced too, but is ignored.
        var stats = tracker.Update(Snap(6_000, 6_000), t + TimeSpan.FromSeconds(1));
        Assert.Equal("game.exe", stats!.App);
        Assert.Equal(100, stats.Fps);

        // Game minimised: its period stops changing, stale after 2 s.
        Assert.NotNull(tracker.Update(Snap(6_000, 7_000), t + TimeSpan.FromSeconds(2.5)));
        Assert.Null(tracker.Update(Snap(6_000, 8_000), t + TimeSpan.FromSeconds(3.5)));

        // RTSS gone.
        Assert.Null(tracker.Update(null, t + TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void Tracker_uses_the_foreground_window_pid_when_rtss_has_no_foreground_info()
    {
        var tracker = new RtssTracker();
        RtssSnapshot Snap(uint tick) => RtssReader.Parse(new Builder(version: 0x0002000E)
            .App(0, 10, "a.exe", tick, tick + 1000, 60, 0)
            .App(1, 20, "b.exe", tick, tick + 900, 120, 0)
            .Bytes)!;

        tracker.Update(Snap(1000), TimeSpan.FromSeconds(1));
        Assert.Equal("a.exe", tracker.Update(Snap(2000), TimeSpan.FromSeconds(2))!.App);
        Assert.Equal("b.exe", tracker.Update(Snap(3000), TimeSpan.FromSeconds(3), foregroundWindowPid: 20)!.App);
    }
}
