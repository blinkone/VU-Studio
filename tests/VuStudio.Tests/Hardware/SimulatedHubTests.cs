using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Hardware.Protocol;
using VuStudio.Hardware.Transport;

namespace VuStudio.Tests.Hardware;

public class SimulatedHubTests
{
    private const string PercentOk = "<0305000400000000";
    private const string EmptyMap = "<07030010" + "00000000000000000000000000000000";

    [Fact]
    public void Uids_are_deterministic_and_distinct()
    {
        var uids = new SimulatedHub(3).GetDials().Select(d => d.Uid).ToList();
        Assert.Equal(["56553153494D000000000001", "56553153494D000000000002", "56553153494D000000000003"], uids);
        Assert.Equal("56553153494D000700000001", new SimulatedHub(1, uidSeed: 7).GetDials()[0].Uid);
    }

    [Fact]
    public void Executes_commands_and_answers_with_status()
    {
        var hub = new SimulatedHub(2);
        Assert.Equal([PercentOk], hub.Process(">030400020132"));
        Assert.Equal(["<1305000400000000"], hub.Process(">130300050101020304"));

        var dial = hub.GetDials()[1];
        Assert.Equal(50, dial.Percent);
        Assert.Equal((int)SimulatedHub.DefaultCalibrationHalf, dial.RawValue);
        Assert.Equal(new LedLevels(1, 2, 3, 4), dial.Backlight);
        Assert.Equal(Easing.DefaultNeedle, dial.NeedleEasing);
        Assert.Equal(Easing.DefaultBacklight, dial.BacklightEasing);
    }

    [Fact]
    public void Answers_queries_with_data()
    {
        var hub = new SimulatedHub(2);
        Assert.Equal(["<07030010" + "01010000000000000000000000000000"], hub.Process(">07010000"));
        Assert.Equal(["<0B03000C56553153494D000000000002"], hub.Process(">0B02000101"));
        Assert.Equal(["<18030010" + "00000002" + "00000032" + "00000005" + "00000064"], hub.Process(">1802000100"));
    }

    [Theory]
    [InlineData(">0304000301", "<030500040000000E")]
    [InlineData(">030400020165", "<0305000400000004")]
    [InlineData(">030400020932", "<0305000400000008")]
    [InlineData(">F5010000", "<F50500040000000C")]
    [InlineData(">2A010000", "<2A0500040000000C")]
    public void Rejects_bad_requests_with_a_status(string request, string reply) =>
        Assert.Equal([reply], new SimulatedHub(2).Process(request));

    [Fact]
    public void Ignores_lines_it_cannot_parse() =>
        Assert.Empty(new SimulatedHub(1).Process("hello"));

    [Fact]
    public void New_dials_appear_after_provisioning_and_a_rescan()
    {
        var hub = new SimulatedHub(2, provisioned: false);
        Assert.Equal(EmptyMap, hub.Process(">07010000").Single());
        Assert.Equal("<0305000400000008", hub.Process(">030400020032").Single());

        hub.Process(">08010000");
        Assert.Equal(EmptyMap, hub.Process(">07010000").Single());
        hub.Process(">0C010000");
        Assert.StartsWith("<070300100101", hub.Process(">07010000").Single());
    }

    [Fact]
    public void Draws_the_image_buffer_and_shows_it()
    {
        var hub = new SimulatedHub(1);
        var uid = hub.GetDials()[0].Uid;

        hub.Process(">0D0200020001");
        hub.Process(">0E020005" + "00" + "0001" + "0008");
        hub.Process(">0F020002" + "00" + "F0");
        Assert.Equal(0, hub.GetDial(uid).RefreshCount);
        Assert.Equal(0, hub.GetDial(uid).GetDisplayedBitmap().CountBlack());

        hub.Process(">1002000100");
        var dial = hub.GetDial(uid);
        var shown = dial.GetDisplayedBitmap();
        Assert.Equal(1, dial.RefreshCount);
        Assert.Equal(4, (MonoBitmap.DialWidth * MonoBitmap.DialHeight) - shown.CountBlack());
        Assert.True(shown[1, 8] && shown[1, 11]);
        Assert.False(shown[1, 7] || shown[1, 12]);
    }

    [Fact]
    public void Image_data_cannot_overflow_the_frame()
    {
        var hub = new SimulatedHub(1);
        hub.Process(">0E020005" + "00" + "00C7" + "0080");
        Assert.Equal("<0F05000400000004", hub.Process(">0F020004" + "00" + "FFFFFF").Single());
        Assert.Equal("<0F05000400000000", hub.Process(">0F020003" + "00" + "FFFF").Single());
        Assert.Equal("<0F05000400000004", hub.Process(">0F0203EA" + "00" + new string('F', 2002)).Single());
    }

    [Fact]
    public void Fault_injection_fails_drops_and_targets_single_dials()
    {
        var hub = new SimulatedHub(2);
        var uids = hub.GetDials().Select(d => d.Uid).ToList();

        hub.FailNextCommands(2, VuStatus.Busy);
        Assert.Equal("<0305000400000002", hub.Process(">030400020010").Single());
        Assert.Equal("<0305000400000002", hub.Process(">030400020010").Single());
        Assert.Equal(0, hub.GetDial(uids[0]).Percent);
        Assert.Equal(PercentOk, hub.Process(">030400020010").Single());

        hub.DropNextReplies(1);
        Assert.Empty(hub.Process(">030400020020"));
        Assert.Equal(32, hub.GetDial(uids[0]).Percent);

        hub.SetDialFault(uids[1], VuStatus.DeviceOffline);
        Assert.Equal("<0305000400000012", hub.Process(">030400020130").Single());
        Assert.Equal(PercentOk, hub.Process(">030400020030").Single());

        var log = hub.GetCommandLog();
        Assert.Equal(6, log.Count);
        Assert.False(log[3].Replied);
        Assert.Equal(1, log[4].BusIndex);
        Assert.Equal(VuStatus.DeviceOffline, log[4].Status);
    }

    [Fact]
    public void Unplugging_cuts_power_but_keeps_the_picture_and_calibration()
    {
        var hub = new SimulatedHub(1);
        var uid = hub.GetDials()[0].Uid;
        hub.Process(">030400020050");
        hub.Process(">13030005" + "00" + "64000000");
        hub.Process(">05040005" + "00" + "0000C350");
        hub.Process(">0D0200020001");
        hub.Process(">1002000100");

        hub.Unplug();
        Assert.Empty(hub.Process(">030400020010"));
        hub.PlugIn();

        var dial = hub.GetDial(uid);
        Assert.Equal(0, dial.Percent);
        Assert.Equal(LedLevels.Off, dial.Backlight);
        Assert.Equal(50000u, dial.CalibrationMax);
        Assert.Equal(MonoBitmap.DialWidth * MonoBitmap.DialHeight, dial.GetDisplayedBitmap().CountBlack());
        Assert.All(dial.ImageBuffer.ToArray(), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void Debug_lines_precede_replies()
    {
        var hub = new SimulatedHub(1) { EmitDebugLines = true };
        var lines = hub.Process(">030400020010");
        Assert.Equal(2, lines.Count);
        Assert.False(VuResponse.TryParse(lines[0], out _));
        Assert.Equal(PercentOk, lines[1]);
    }

    [Fact]
    public async Task A_transport_cannot_open_while_unplugged()
    {
        var hub = new SimulatedHub(1);
        hub.Unplug();
        await using var transport = new SimulatedVuTransport(hub);
        await Assert.ThrowsAsync<IOException>(() => transport.OpenAsync(TestContext.Current.CancellationToken));
        Assert.False(transport.IsOpen);
    }
}
