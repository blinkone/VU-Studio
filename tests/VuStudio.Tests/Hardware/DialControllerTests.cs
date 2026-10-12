using System.Collections.Concurrent;
using System.Diagnostics;
using VuStudio.Core.Engine;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Hardware;
using VuStudio.Hardware.Discovery;
using VuStudio.Hardware.Protocol;
using VuStudio.Hardware.Transport;

namespace VuStudio.Tests.Hardware;

public sealed class DialControllerTests : IAsyncDisposable
{
    /// <summary>Production behaviour with short delays so the tests stay fast.</summary>
    private static readonly DialControllerOptions Fast = new()
    {
        Timeouts = new VuTimeouts
        {
            Value = TimeSpan.FromMilliseconds(250),
            Command = TimeSpan.FromMilliseconds(250),
            Display = TimeSpan.FromMilliseconds(250),
            Bus = TimeSpan.FromMilliseconds(250),
        },
        ProbeTimeout = TimeSpan.FromMilliseconds(150),
        InterChunkDelay = TimeSpan.FromMilliseconds(20),
        NeedleInterval = TimeSpan.FromMilliseconds(5),
        ReconnectDelay = TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
        RetryDelay = TimeSpan.FromMilliseconds(10),
        MaxRetryDelay = TimeSpan.FromMilliseconds(40),
        IdentifyDuration = TimeSpan.FromMilliseconds(600),
    };

    private readonly List<DialController> _controllers = [];
    private readonly TestLogger<DialController> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var controller in _controllers)
        {
            await controller.DisposeAsync();
        }

        Assert.Empty(_logger.Errors);
    }

    [Fact]
    public async Task Start_discovers_the_dials_by_uid()
    {
        var hub = new SimulatedHub(3);
        var changes = 0;
        var controller = Create(hub);
        controller.Changed += () => Interlocked.Increment(ref changes);

        await controller.StartAsync(Ct);
        await WaitConnectedAsync(controller, dials: 3);

        Assert.Equal(hub.GetDials().Select(d => d.Uid), controller.Dials.Select(d => d.Uid));
        Assert.Equal([0, 1, 2], controller.Dials.Select(d => d.BusIndex));
        Assert.All(controller.Dials, d =>
        {
            Assert.True(d.Online);
            Assert.Equal("1.0.0-sim", d.FirmwareVersion);
            Assert.Equal("VU1-SIM rev.A", d.HardwareVersion);
            Assert.Equal("1", d.ProtocolVersion);
            Assert.Equal("5139a7e", d.BuildHash);
            Assert.Equal(Easing.DefaultNeedle, d.NeedleEasing);
            Assert.Equal(Easing.DefaultBacklight, d.BacklightEasing);
        });
        Assert.Equal(new HubStatus(HubConnectionState.Connected, "simulator", "3 dial(s)", Simulated: true), controller.Status);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Needles_backlights_and_easing_reach_the_dials()
    {
        var hub = new SimulatedHub(2);
        var (first, second) = (hub.GetDials()[0].Uid, hub.GetDials()[1].Uid);
        var controller = await StartAsync(hub);

        controller.SetNeedle(first, 42.5);
        controller.SetNeedle(second, 250);
        controller.SetBacklight(first, new LedLevels(10, 20, 30, 40));
        controller.SetBacklight(second, new LedLevels(200, 0, 0, 0));
        controller.SetNeedleEasing(first, new Easing(5, 30));
        controller.SetBacklightTransition(second, 500);

        await Eventually.HoldsAsync(
            () => hub.GetDial(first).Percent == 43 && hub.GetDial(second).Percent == 100 &&
                  hub.GetDial(first).Backlight == new LedLevels(10, 20, 30, 40) && hub.GetDial(second).Backlight == new LedLevels(100, 0, 0, 0) &&
                  hub.GetDial(first).NeedleEasing == new Easing(5, 30) && hub.GetDial(second).BacklightEasing == new Easing(4, 20),
            "the simulator shows every target");
        await Eventually.HoldsAsync(() => controller.Dials[0].NeedleEasing == new Easing(5, 30), "the dial info shows the new easing");
    }

    [Fact]
    public async Task Only_the_latest_value_is_sent_and_unchanged_values_are_skipped()
    {
        var hub = new SimulatedHub(2);
        var (first, second) = (hub.GetDials()[0].Uid, hub.GetDials()[1].Uid);
        var controller = await StartAsync(hub);

        for (var i = 0; i <= 100; i++)
        {
            controller.SetNeedle(first, i);
        }

        await Eventually.HoldsAsync(() => hub.GetDial(first).Percent == 100, "the last value arrives");
        var sent = NeedleCommands(hub, 0);
        Assert.InRange(sent, 1, 20);

        controller.SetNeedle(first, 99.6);
        controller.SetNeedle(second, 5);
        await Eventually.HoldsAsync(() => hub.GetDial(second).Percent == 5, "the worker ran another cycle");
        Assert.Equal(sent, NeedleCommands(hub, 0));
    }

    [Fact]
    public async Task A_face_upload_shows_the_same_bitmap()
    {
        var hub = new SimulatedHub(2);
        var uid = hub.GetDials()[1].Uid;
        var controller = await StartAsync(hub);
        var face = TestFaces.Pattern();

        controller.SetFace(uid, face);
        await Eventually.HoldsAsync(() => hub.GetDial(uid).RefreshCount == 1, "the face is shown");

        Assert.Equal(face, hub.GetDial(uid).GetDisplayedBitmap());
        var display = hub.GetCommandLog().Where(c => c.BusIndex == 1 && IsDisplay(c.Command)).ToList();
        Assert.Equal(
            [VuCommand.DisplayClear, VuCommand.DisplayGotoXY, VuCommand.DisplayImageData, VuCommand.DisplayImageData,
             VuCommand.DisplayImageData, VuCommand.DisplayImageData, VuCommand.DisplayShowImage],
            display.Select(c => c.Command));
        Assert.Equal([1001, 1001, 1001, 601], display.Where(c => c.Command == VuCommand.DisplayImageData).Select(c => c.Payload.Length));
        Assert.Equal(0, hub.GetDials()[0].RefreshCount);
    }

    [Fact]
    public async Task An_identical_face_is_not_uploaded_again()
    {
        var hub = new SimulatedHub(2);
        var (first, second) = (hub.GetDials()[0].Uid, hub.GetDials()[1].Uid);
        var controller = await StartAsync(hub);
        var face = TestFaces.Pattern();

        controller.SetFace(first, face);
        await Eventually.HoldsAsync(() => hub.GetDial(first).RefreshCount == 1, "the face is shown");

        controller.SetFace(first, face.Clone());
        controller.SetNeedle(second, 64);
        await Eventually.HoldsAsync(() => hub.GetDial(second).Percent == 64, "the worker ran another cycle");
        await Task.Delay(Fast.InterChunkDelay * 3, Ct);
        Assert.Equal(1, hub.GetDial(first).RefreshCount);
        Assert.Equal(1, hub.GetCommandLog().Count(c => c.Command == VuCommand.DisplayClear));

        var changed = face.Clone();
        changed[100, 100] = !changed[100, 100];
        controller.SetFace(first, changed);
        await Eventually.HoldsAsync(() => hub.GetDial(first).RefreshCount == 2, "a different face is shown");
        Assert.Equal(changed, hub.GetDial(first).GetDisplayedBitmap());
    }

    [Fact]
    public async Task Needles_stay_live_while_a_face_uploads()
    {
        var hub = new SimulatedHub(2);
        var (first, second) = (hub.GetDials()[0].Uid, hub.GetDials()[1].Uid);
        var controller = await StartAsync(hub, Fast with { InterChunkDelay = TimeSpan.FromMilliseconds(100) });
        var fired = 0;
        hub.CommandProcessed += command =>
        {
            if (command.Command == VuCommand.DisplayImageData && Interlocked.Exchange(ref fired, 1) == 0)
            {
                controller.SetNeedle(first, 61);
                controller.SetNeedle(second, 62);
                controller.SetBacklight(second, new LedLevels(1, 2, 3, 4));
            }
        };

        controller.SetFace(first, TestFaces.Pattern());
        await Eventually.HoldsAsync(() => hub.GetDial(first).RefreshCount == 1, "the face is shown");

        var log = hub.GetCommandLog();
        var frames = log.Where(c => c.Command == VuCommand.DisplayImageData).Select(c => c.Sequence).ToList();
        var show = log.Single(c => c.Command == VuCommand.DisplayShowImage).Sequence;
        var needle1 = log.Single(c => c.Command == VuCommand.SetDialPercentSingle && c.Payload.Span[1] == 61).Sequence;
        var needle2 = log.Single(c => c.Command == VuCommand.SetDialPercentSingle && c.Payload.Span[1] == 62).Sequence;
        var light = log.Single(c => c.Command == VuCommand.SetRgbBacklight && c.BusIndex == 1).Sequence;

        Assert.All([needle1, needle2, light], sequence => Assert.InRange(sequence, frames[0] + 1, frames[1] - 1));
        Assert.True(frames[^1] < show);
    }

    [Fact]
    public async Task Reconnects_after_an_unplug_and_restores_the_state_by_uid()
    {
        var hub = new SimulatedHub(2);
        var (first, second) = (hub.GetDials()[0].Uid, hub.GetDials()[1].Uid);
        var controller = await StartAsync(hub);
        var face = TestFaces.Pattern();
        var states = new ConcurrentQueue<HubConnectionState>();
        controller.Changed += () => states.Enqueue(controller.Status.State);

        controller.SetNeedle(first, 60);
        controller.SetNeedle(second, 30);
        controller.SetBacklight(first, new LedLevels(5, 6, 7, 8));
        controller.SetNeedleEasing(second, new Easing(9, 45));
        controller.SetFace(first, face);
        await Eventually.HoldsAsync(
            () => hub.GetDial(first).Percent == 60 && hub.GetDial(second).Percent == 30 && hub.GetDial(first).RefreshCount == 1 &&
                  hub.GetDial(second).NeedleEasing == new Easing(9, 45),
            "the state is applied");

        hub.Unplug();
        await Eventually.HoldsAsync(() => controller.Status.State != HubConnectionState.Connected, "the controller notices the unplug");
        await Eventually.HoldsAsync(() => controller.Dials.All(d => !d.Online), "the dials go offline");
        Assert.Equal(0, hub.GetDial(first).Percent);

        hub.SwapBusIndices(first, second);
        hub.PlugIn();

        await WaitConnectedAsync(controller, dials: 2);
        await Eventually.HoldsAsync(
            () => hub.GetDial(first).Percent == 60 && hub.GetDial(second).Percent == 30 &&
                  hub.GetDial(first).Backlight == new LedLevels(5, 6, 7, 8) && hub.GetDial(second).NeedleEasing == new Easing(9, 45) &&
                  hub.GetDial(first).RefreshCount == 2,
            "the state is restored on the right dials");
        Assert.Equal(1, controller.Dials.Single(d => d.Uid == first).BusIndex);
        Assert.Equal(1, hub.GetDial(first).BusIndex);
        Assert.Equal(face, hub.GetDial(first).GetDisplayedBitmap());
        Assert.Contains(HubConnectionState.Disconnected, states);
    }

    [Fact]
    public async Task A_failing_dial_backs_off_goes_offline_and_recovers_on_rescan()
    {
        var hub = new SimulatedHub(2);
        var (first, second) = (hub.GetDials()[0].Uid, hub.GetDials()[1].Uid);
        var controller = await StartAsync(hub);

        hub.SetDialFault(second, VuStatus.DeviceOffline);
        controller.SetNeedle(second, 50);
        controller.SetNeedle(first, 40);

        await Eventually.HoldsAsync(() => !controller.Dials.Single(d => d.Uid == second).Online, "the failing dial goes offline");
        Assert.Equal(40, hub.GetDial(first).Percent);
        Assert.Equal(5, NeedleCommands(hub, 1));
        var attempts = hub.GetCommandLog().Where(c => c.BusIndex == 1 && c.Command == VuCommand.SetDialPercentSingle).Select(c => c.Sequence).ToList();
        Assert.True(attempts.Zip(attempts.Skip(1)).All(pair => pair.Second > pair.First + 1) || attempts.Count == 5);

        controller.SetNeedle(second, 55);
        controller.SetNeedle(first, 41);
        await Eventually.HoldsAsync(() => hub.GetDial(first).Percent == 41, "the healthy dial still works");
        Assert.Equal(5, NeedleCommands(hub, 1));

        hub.SetDialFault(second, null);
        await controller.RescanAsync(provision: false, Ct);
        await Eventually.HoldsAsync(() => controller.Dials.All(d => d.Online) && hub.GetDial(second).Percent == 55, "the dial is back");
    }

    [Fact]
    public async Task Unresponsive_dials_recover_on_the_periodic_rescan()
    {
        var hub = new SimulatedHub(1);
        var uid = hub.GetDials()[0].Uid;
        var controller = await StartAsync(hub, Fast with { RecoveryInterval = TimeSpan.FromMilliseconds(200) });

        hub.SetDialFault(uid, VuStatus.I2CError);
        controller.SetNeedle(uid, 12);
        await Eventually.HoldsAsync(() => !controller.Dials[0].Online, "the dial goes offline");

        hub.SetDialFault(uid, null);
        await Eventually.HoldsAsync(() => controller.Dials[0].Online && hub.GetDial(uid).Percent == 12, "the dial recovers by itself");
    }

    [Fact]
    public async Task Provisions_when_the_bus_is_empty()
    {
        var hub = new SimulatedHub(2, provisioned: false);
        var controller = await StartAsync(hub);

        Assert.Equal(2, controller.Dials.Count);
        Assert.Equal(3, hub.GetCommandLog().Count(c => c.Command == VuCommand.ProvisionDevice));
    }

    [Fact]
    public async Task Identify_wiggles_flashes_and_restores()
    {
        var hub = new SimulatedHub(1);
        var uid = hub.GetDials()[0].Uid;
        var controller = await StartAsync(hub);
        controller.SetNeedle(uid, 40);
        controller.SetBacklight(uid, new LedLevels(0, 0, 50, 0));
        await Eventually.HoldsAsync(() => hub.GetDial(uid).Backlight == new LedLevels(0, 0, 50, 0), "the state is applied");

        var flashes = 0;
        var wiggles = 0;
        hub.CommandProcessed += command =>
        {
            if (command.Command == VuCommand.SetRgbBacklight && command.Payload.Span[1..].SequenceEqual(new byte[] { 100, 100, 100, 100 }))
            {
                Interlocked.Increment(ref flashes);
            }
            else if (command.Command == VuCommand.SetDialPercentSingle && command.Payload.Span[1] is 25 or 75)
            {
                Interlocked.Increment(ref wiggles);
            }
        };

        var watch = Stopwatch.StartNew();
        await controller.IdentifyAsync(uid, Ct);

        Assert.True(watch.Elapsed >= Fast.IdentifyDuration * 0.8, $"identify took only {watch.Elapsed}");
        Assert.True(flashes >= 2, $"{flashes} flashes");
        Assert.True(wiggles >= 2, $"{wiggles} wiggles");
        await Eventually.HoldsAsync(
            () => hub.GetDial(uid).Percent == 40 && hub.GetDial(uid).Backlight == new LedLevels(0, 0, 50, 0) &&
                  hub.GetDial(uid).NeedleEasing == Easing.DefaultNeedle && hub.GetDial(uid).BacklightEasing == Easing.DefaultBacklight,
            "the desired state and easing are restored");

        await controller.IdentifyAsync("0000", Ct);
    }

    [Fact]
    public async Task Raw_moves_and_calibration_go_through_the_worker()
    {
        var hub = new SimulatedHub(1);
        var uid = hub.GetDials()[0].Uid;
        var controller = Create(hub);
        Assert.False(await controller.SetRawAsync(uid, 100, Ct));

        await controller.StartAsync(Ct);
        await WaitConnectedAsync(controller, dials: 1);

        Assert.True(await controller.SetRawAsync(uid, 12345, Ct));
        Assert.Equal(12345, hub.GetDial(uid).RawValue);
        Assert.True(await controller.CalibrateAsync(uid, CalibrationPoint.Full, 50000, Ct));
        Assert.True(await controller.CalibrateAsync(uid, CalibrationPoint.Half, 25000, Ct));
        Assert.Equal(50000u, hub.GetDial(uid).CalibrationMax);
        Assert.Equal(25000u, hub.GetDial(uid).CalibrationHalf);
        Assert.False(await controller.SetRawAsync("0000", 1, Ct));

        hub.FailNextCommands(1, VuStatus.Busy);
        Assert.False(await controller.CalibrateAsync(uid, CalibrationPoint.Full, 1, Ct));
    }

    [Fact]
    public async Task Invalidate_sends_everything_again()
    {
        var hub = new SimulatedHub(1);
        var uid = hub.GetDials()[0].Uid;
        var controller = await StartAsync(hub);
        controller.SetNeedle(uid, 33);
        controller.SetFace(uid, TestFaces.Pattern());
        await Eventually.HoldsAsync(() => hub.GetDial(uid).Percent == 33 && hub.GetDial(uid).RefreshCount == 1, "the state is applied");

        hub.Process(">030400020005");
        Assert.Equal(5, hub.GetDial(uid).Percent);

        controller.Invalidate();
        await Eventually.HoldsAsync(() => hub.GetDial(uid).Percent == 33 && hub.GetDial(uid).RefreshCount == 2, "everything is sent again");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_parks_the_dials_when_asked(bool park)
    {
        var hub = new SimulatedHub(2);
        var uid = hub.GetDials()[1].Uid;
        var controller = await StartAsync(hub, Fast with { ParkOnStop = park });
        controller.SetNeedle(uid, 70);
        controller.SetBacklight(uid, new LedLevels(50, 0, 0, 0));
        await Eventually.HoldsAsync(() => hub.GetDial(uid).Backlight == new LedLevels(50, 0, 0, 0), "the state is applied");

        await controller.StopAsync(Ct);

        Assert.Equal(park ? 0 : 70, hub.GetDial(uid).Percent);
        Assert.Equal(park ? LedLevels.Off : new LedLevels(50, 0, 0, 0), hub.GetDial(uid).Backlight);
        Assert.Equal(HubStatus.Disconnected, controller.Status);
        Assert.All(controller.Dials, d => Assert.False(d.Online));
    }

    [Fact]
    public async Task A_hub_that_does_not_answer_is_retried()
    {
        var hub = new SimulatedHub(1);
        hub.DropNextReplies(1);
        var states = new ConcurrentQueue<HubStatus>();
        var controller = Create(hub);
        controller.Changed += () => states.Enqueue(controller.Status);

        await controller.StartAsync(Ct);
        await WaitConnectedAsync(controller, dials: 1);

        var error = Assert.Single(states, s => s.State == HubConnectionState.Error);
        Assert.Contains("does not respond", error.Message);
    }

    [Fact]
    public async Task Discovery_probes_candidates_and_skips_ports_that_stay_silent()
    {
        var silent = new SimulatedHub(1, uidSeed: 1);
        silent.DropNextReplies(int.MaxValue);
        var hub = new SimulatedHub(2);
        var locator = new FixedPortLocator(new HubPortCandidate("COM3", "other FT231X"), new HubPortCandidate("COM4", "hub"));
        var controller = Create(locator: locator, ports: port => new SimulatedVuTransport(port == "COM4" ? hub : silent));

        await controller.StartAsync(Ct);
        await WaitConnectedAsync(controller, dials: 2);

        Assert.Equal("COM4", controller.Status.Port);
        Assert.Equal([VuCommand.GetDevicesMap], silent.GetCommandLog().Select(c => c.Command));
        Assert.Equal(VuCommand.GetDevicesMap, hub.GetCommandLog()[0].Command);
        Assert.Equal(VuCommand.RescanBus, hub.GetCommandLog()[1].Command);
    }

    [Fact]
    public async Task A_configured_port_is_used_without_a_probe()
    {
        var hub = new SimulatedHub(1);
        var locator = new FixedPortLocator(new HubPortCandidate("COM3", "discovered"));
        var controller = Create(Fast with { Port = "COM7" }, locator: locator, ports: port => new SimulatedVuTransport(port == "COM7" ? hub : new SimulatedHub(0)));

        await controller.StartAsync(Ct);
        await WaitConnectedAsync(controller, dials: 1);

        Assert.Equal("COM7", controller.Status.Port);
        Assert.Equal(VuCommand.RescanBus, hub.GetCommandLog()[0].Command);
    }

    [Fact]
    public async Task Reports_a_missing_hub()
    {
        var controller = Create(locator: new FixedPortLocator());

        await controller.StartAsync(Ct);
        await Eventually.HoldsAsync(() => controller.Status.Message is not null, "the status explains the problem");

        Assert.Equal(HubConnectionState.Disconnected, controller.Status.State);
        Assert.Contains("not found", controller.Status.Message);
        Assert.False(await controller.SetRawAsync("0000", 1, Ct));
        await controller.IdentifyAsync("0000", Ct);
        await controller.RescanAsync(provision: true, Ct);
    }

    [Fact]
    public async Task Simulate_builds_an_in_process_hub_and_options_reconnect()
    {
        var controller = new DialController(Fast with { Simulate = 2 }, _logger);
        _controllers.Add(controller);

        await controller.StartAsync(Ct);
        await WaitConnectedAsync(controller, dials: 2);
        Assert.NotNull(controller.Simulator);
        Assert.True(controller.Status.Simulated);

        controller.SetNeedle(controller.Dials[0].Uid, 80);
        await Eventually.HoldsAsync(() => controller.Simulator!.GetDials()[0].Percent == 80, "the needle reaches the simulator");

        controller.ApplyOptions(controller.Options with { Simulate = 3 });
        await Eventually.HoldsAsync(() => controller.Dials.Count(d => d.Online) == 3, "the bigger simulator is connected");
        Assert.Equal(3, controller.Simulator!.DialCount);
        await Eventually.HoldsAsync(() => controller.Simulator!.GetDials()[0].Percent == 80, "the state follows the dial");
    }

    [Fact]
    public async Task Setters_validate_and_normalize()
    {
        var hub = new SimulatedHub(1);
        var uid = hub.GetDials()[0].Uid;
        var controller = await StartAsync(hub);

        Assert.Throws<ArgumentException>(() => controller.SetNeedle("", 1));
        Assert.Throws<ArgumentNullException>(() => controller.SetFace(uid, null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetRawAsync(uid, 70000, Ct));

        controller.SetNeedle(uid, double.NaN);
        controller.SetNeedle(uid, -5);
        controller.SetNeedleEasing(uid, new Easing(0, 0));
        await Eventually.HoldsAsync(() => hub.GetDial(uid).NeedleEasing == new Easing(1, 1), "the easing is clamped");
        Assert.Equal(0, hub.GetDial(uid).Percent);
    }

    private static bool IsDisplay(VuCommand command) =>
        command is VuCommand.DisplayClear or VuCommand.DisplayGotoXY or VuCommand.DisplayImageData or VuCommand.DisplayShowImage;

    private static int NeedleCommands(SimulatedHub hub, int busIndex) =>
        hub.GetCommandLog().Count(c => c.Command == VuCommand.SetDialPercentSingle && c.BusIndex == busIndex);

    private static Task WaitConnectedAsync(DialController controller, int dials) =>
        Eventually.HoldsAsync(
            () => controller.Status.State == HubConnectionState.Connected && controller.Dials.Count(d => d.Online) == dials,
            $"connected with {dials} dial(s)");

    private DialController Create(
        SimulatedHub? hub = null,
        DialControllerOptions? options = null,
        IHubPortLocator? locator = null,
        Func<string, IVuTransport>? ports = null)
    {
        var controller = hub is null
            ? new DialController(options ?? Fast, _logger, portLocator: locator) { PortTransportFactory = ports ?? (_ => throw new IOException("No such port.")) }
            : new DialController(options ?? Fast, _logger, transportFactory: () => new SimulatedVuTransport(hub));
        _controllers.Add(controller);
        return controller;
    }

    private DialController Create(DialControllerOptions options, IHubPortLocator locator, Func<string, IVuTransport> ports) =>
        Create(hub: null, options, locator, ports);

    private async Task<DialController> StartAsync(SimulatedHub hub, DialControllerOptions? options = null)
    {
        var controller = Create(hub, options);
        await controller.StartAsync(Ct);
        await WaitConnectedAsync(controller, hub.DialCount);
        return controller;
    }
}
