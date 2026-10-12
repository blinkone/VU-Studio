using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Hardware;
using VuStudio.Hardware.Protocol;
using VuStudio.Hardware.Transport;

namespace VuStudio.Tests.Hardware;

public sealed class VuHubClientTests : IAsyncDisposable
{
    private static readonly VuTimeouts Fast = new()
    {
        Value = TimeSpan.FromMilliseconds(300),
        Command = TimeSpan.FromMilliseconds(300),
        Display = TimeSpan.FromMilliseconds(300),
        Bus = TimeSpan.FromMilliseconds(300),
    };

    private readonly SimulatedHub _hub = new(3);
    private readonly SimulatedVuTransport _transport;
    private readonly VuHubClient _client;

    public VuHubClientTests()
    {
        _transport = new SimulatedVuTransport(_hub);
        _client = new VuHubClient(_transport, Fast, new TestLogger<VuHubClient>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Uid(int index) => _hub.GetDials()[index].Uid;

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _transport.DisposeAsync();
    }

    [Fact]
    public async Task Enumerates_the_bus_and_reads_dial_info()
    {
        await _transport.OpenAsync(Ct);
        await _client.RescanBusAsync(Ct);

        Assert.Equal([0, 1, 2], await _client.GetDevicesMapAsync(Ct));
        Assert.Equal("56553153494D000000000002", await _client.GetUidAsync(1, Ct));
        Assert.Equal("1.0.0-sim", await _client.GetFirmwareInfoAsync(0, Ct));
        Assert.Equal("VU1-SIM rev.A", await _client.GetHardwareInfoAsync(0, Ct));
        Assert.Equal("1", await _client.GetProtocolInfoAsync(0, Ct));
        Assert.Equal("5139a7e", await _client.GetBuildInfoAsync(0, Ct));
    }

    [Fact]
    public async Task Sets_needles_backlights_and_easing()
    {
        await _transport.OpenAsync(Ct);
        await _client.SetPercentAsync(0, 42, Ct);
        await _client.SetPercentMultipleAsync([(1, 10), (2, 90)], Ct);
        await _client.SetBacklightAsync(1, new LedLevels(100, 50, 0, 25), Ct);
        await _client.SetNeedleEasingStepAsync(2, 7, Ct);
        await _client.SetNeedleEasingPeriodAsync(2, 30, Ct);
        await _client.SetBacklightEasingStepAsync(2, 9, Ct);
        await _client.SetBacklightEasingPeriodAsync(2, 40, Ct);

        Assert.Equal([42, 10, 90], _hub.GetDials().Select(d => d.Percent));
        Assert.Equal(new LedLevels(100, 50, 0, 25), _hub.GetDial(Uid(1)).Backlight);
        var easing = await _client.GetEasingConfigAsync(2, Ct);
        Assert.Equal(new VuEasingConfig(7, 30, 9, 40), easing);
        Assert.Equal(new Easing(7, 30), easing.Needle);
        Assert.Equal(Easing.DefaultBacklight, (await _client.GetEasingConfigAsync(0, Ct)).Backlight);
    }

    [Fact]
    public async Task Moves_raw_and_stores_calibration()
    {
        await _transport.OpenAsync(Ct);
        await _client.SetRawAsync(0, 0xBEEF, Ct);
        await _client.CalibrateMaxAsync(0, 50000, Ct);
        await _client.CalibrateHalfAsync(0, 24000, Ct);

        var dial = _hub.GetDial(Uid(0));
        Assert.Equal(0xBEEF, dial.RawValue);
        Assert.Equal(50000u, dial.CalibrationMax);
        Assert.Equal(24000u, dial.CalibrationHalf);
    }

    [Fact]
    public async Task Uploads_an_image_in_frames()
    {
        await _transport.OpenAsync(Ct);
        var face = TestFaces.Pattern();
        var frame = EInkEncoder.Encode(face);

        await _client.DisplayClearAsync(2, black: false, Ct);
        await _client.DisplayGotoXYAsync(2, 0, 0, Ct);
        foreach (var chunk in frame.Chunk(VuProtocol.MaxImageChunk))
        {
            await _client.DisplayImageDataAsync(2, chunk, Ct);
        }

        Assert.Equal(0, _hub.GetDial(Uid(2)).RefreshCount);
        await _client.DisplayShowAsync(2, Ct);

        var dial = _hub.GetDial(Uid(2));
        Assert.Equal(1, dial.RefreshCount);
        Assert.Equal(face, dial.GetDisplayedBitmap());
    }

    [Fact]
    public async Task A_non_ok_status_raises_a_status_exception()
    {
        await _transport.OpenAsync(Ct);
        _hub.FailNextCommands(1, VuStatus.I2CError);

        var error = await Assert.ThrowsAsync<VuStatusException>(() => _client.SetBacklightAsync(0, 1, 2, 3, 4, Ct));
        Assert.Equal(VuStatus.I2CError, error.Status);
        Assert.Equal(VuCommand.SetRgbBacklight, error.Command);

        await _client.SetBacklightAsync(0, 1, 2, 3, 4, Ct);
        Assert.Equal(new LedLevels(1, 2, 3, 4), _hub.GetDial(Uid(0)).Backlight);
    }

    [Fact]
    public async Task A_status_instead_of_data_is_an_error()
    {
        await _transport.OpenAsync(Ct);
        _hub.FailNextCommands(1, VuStatus.Ok);
        await Assert.ThrowsAsync<VuHubException>(() => _client.GetUidAsync(0, Ct));
    }

    [Fact]
    public async Task A_missing_reply_times_out_and_the_next_command_works()
    {
        await _transport.OpenAsync(Ct);
        _hub.DropNextReplies(1);

        var error = await Assert.ThrowsAsync<VuTimeoutException>(() => _client.SetPercentAsync(0, 10, Ct));
        Assert.Equal(Fast.Value, error.Timeout);

        await _client.SetPercentAsync(0, 20, Ct);
        Assert.Equal(20, _hub.GetDial(Uid(0)).Percent);
    }

    [Fact]
    public async Task A_late_reply_is_not_mistaken_for_the_next_one()
    {
        await _transport.OpenAsync(Ct);
        _hub.Latency = TimeSpan.FromMilliseconds(150);
        _client.Timeouts = Fast with { Value = TimeSpan.FromMilliseconds(40), Command = TimeSpan.FromSeconds(2) };

        await Assert.ThrowsAsync<VuTimeoutException>(() => _client.SetPercentAsync(0, 10, Ct));

        // The percent reply arrives while the client waits for the UID reply and must be skipped.
        Assert.Equal("56553153494D000000000001", await _client.GetUidAsync(0, Ct));
        Assert.Equal(2, _hub.CommandCount);
    }

    [Fact]
    public async Task Debug_output_between_replies_is_ignored()
    {
        await _transport.OpenAsync(Ct);
        _hub.EmitDebugLines = true;

        await _client.SetPercentAsync(1, 77, Ct);
        Assert.Equal([0, 1, 2], await _client.GetDevicesMapAsync(Ct));
        Assert.Equal(77, _hub.GetDial(Uid(1)).Percent);
    }

    [Fact]
    public async Task Concurrent_commands_run_one_at_a_time()
    {
        await _transport.OpenAsync(Ct);
        _hub.Latency = TimeSpan.FromMilliseconds(2);

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => _client.SetPercentAsync(i % 3, i, Ct)));

        Assert.Equal(30, _hub.CommandCount);
        Assert.All(_hub.GetCommandLog(), c => Assert.True(c.Replied && c.Status == VuStatus.Ok));
    }

    [Fact]
    public async Task Unplugging_fails_commands_with_an_io_error()
    {
        await _transport.OpenAsync(Ct);
        Exception? lost = null;
        _transport.Disconnected += error => lost = error;

        _hub.Unplug();

        Assert.IsType<IOException>(lost);
        Assert.False(_transport.IsOpen);
        await Assert.ThrowsAsync<IOException>(() => _client.SetPercentAsync(0, 10, Ct));
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_sending()
    {
        await _transport.OpenAsync(Ct);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SetPercentAsync(0, 101, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SetPercentAsync(256, 1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SetBacklightAsync(0, 0, 0, 0, 101, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SetRawAsync(0, 65536, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.DisplayImageDataAsync(0, new byte[1001], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.SendAsync((VuCommand)0xF5, VuDataType.None, ReadOnlyMemory<byte>.Empty, Fast.Command, Ct));
        Assert.Equal(0, _hub.CommandCount);
    }
}
