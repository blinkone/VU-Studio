using VuStudio.Hardware.Protocol;

namespace VuStudio.Tests.Hardware;

public class VuProtocolTests
{
    [Fact]
    public void Encodes_the_documented_examples()
    {
        Assert.Equal(">030400020132", VuProtocol.EncodeRequest(VuCommand.SetDialPercentSingle, [0x01, 0x32]));
        Assert.Equal(">0C010000", VuProtocol.EncodeRequest(VuCommand.RescanBus, []));
        Assert.Equal(">07010000", VuProtocol.EncodeRequest(VuCommand.GetDevicesMap, []));
    }

    [Fact]
    public void Encodes_payload_and_length_as_uppercase_hex()
    {
        Assert.Equal(">1303000501ABCDEF0A", VuProtocol.EncodeRequest(VuCommand.SetRgbBacklight, [0x01, 0xAB, 0xCD, 0xEF, 0x0A]));
        Assert.Equal(">0F02012C" + new string('F', 600), VuProtocol.EncodeRequest(VuCommand.DisplayImageData, Enumerable.Repeat((byte)0xFF, 300).ToArray()));
    }

    [Fact]
    public void Encodes_status_replies() =>
        Assert.Equal("<1305000400000001", VuProtocol.EncodeStatusResponse(VuCommand.SetRgbBacklight, VuStatus.Fail));

    [Theory]
    [InlineData(VuCommand.SetDialRawSingle, VuDataType.KeyValuePair)]
    [InlineData(VuCommand.SetDialPercentMultiple, VuDataType.KeyValuePair)]
    [InlineData(VuCommand.SetDialCalibrateHalf, VuDataType.KeyValuePair)]
    [InlineData(VuCommand.GetDevicesMap, VuDataType.None)]
    [InlineData(VuCommand.ProvisionDevice, VuDataType.None)]
    [InlineData(VuCommand.RescanBus, VuDataType.None)]
    [InlineData(VuCommand.DialPower, VuDataType.SingleValue)]
    [InlineData(VuCommand.DisplayImageData, VuDataType.SingleValue)]
    [InlineData(VuCommand.SetRgbBacklight, VuDataType.MultipleValue)]
    [InlineData(VuCommand.GetEasingConfig, VuDataType.SingleValue)]
    [InlineData(VuCommand.GetProtocolInfo, VuDataType.SingleValue)]
    public void Uses_the_documented_data_type(VuCommand command, VuDataType expected) =>
        Assert.Equal(expected, VuProtocol.DefaultDataType(command));

    [Fact]
    public void Command_codes_jump_from_0x19_to_0x20()
    {
        Assert.Equal(0x18, (byte)VuCommand.GetEasingConfig);
        Assert.Equal(0x19, (byte)VuCommand.GetBuildInfo);
        Assert.Equal(0x20, (byte)VuCommand.GetFirmwareInfo);
        Assert.Equal(0x21, (byte)VuCommand.GetHardwareInfo);
        Assert.Equal(0x22, (byte)VuCommand.GetProtocolInfo);
        Assert.False(Enum.IsDefined((VuCommand)0x1A));
    }

    [Fact]
    public void Status_codes_match_the_table()
    {
        Assert.Equal(0x000Eu, (uint)VuStatus.MalformedPackage);
        Assert.Equal(0x0012u, (uint)VuStatus.DeviceOffline);
        Assert.Equal(0xE003u, (uint)VuStatus.BootloaderInvalidRequest);
    }

    [Theory]
    [InlineData(0xF4)]
    [InlineData(0xF9)]
    [InlineData(0xFD)]
    public void Refuses_bootloader_commands(byte code) =>
        Assert.Throws<ArgumentException>(() => VuProtocol.EncodeRequest((VuCommand)code, []));

    [Theory]
    [InlineData("<0305000400000000", VuCommand.SetDialPercentSingle, VuStatus.Ok)]
    [InlineData("<1305000400000001", VuCommand.SetRgbBacklight, VuStatus.Fail)]
    [InlineData("<0C0500040000000E\r\n", VuCommand.RescanBus, VuStatus.MalformedPackage)]
    [InlineData("  <0b05000400000012", VuCommand.GetDeviceUid, VuStatus.DeviceOffline)]
    public void Parses_status_replies(string line, VuCommand command, VuStatus status)
    {
        Assert.True(VuResponse.TryParse(line, out var response));
        Assert.Equal(command, response.Command);
        Assert.True(response.IsStatus);
        Assert.Equal(status, response.Status);
    }

    [Fact]
    public void Parses_data_replies_and_treats_the_length_as_advisory()
    {
        const string uid = "56553153494D000000000001";
        Assert.True(VuResponse.TryParse($"<0B03000C{uid}", out var exact));
        Assert.True(VuResponse.TryParse($"<0B0300FF{uid}", out var wrongLength));
        Assert.True(VuResponse.TryParse($"<0B030000{uid}", out var zeroLength));

        foreach (var response in new[] { exact, wrongLength, zeroLength })
        {
            Assert.Equal(VuCommand.GetDeviceUid, response.Command);
            Assert.False(response.IsStatus);
            Assert.Null(response.Status);
            Assert.Equal(uid, Convert.ToHexString(response.Payload.Span));
        }

        Assert.Equal(0xFF, wrongLength.DeclaredLength);
    }

    [Fact]
    public void Parses_an_empty_payload()
    {
        Assert.True(VuResponse.TryParse("<07030000", out var response));
        Assert.Equal(0, response.Payload.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<")]
    [InlineData("<0305")]
    [InlineData("<03050004")]
    [InlineData("<030500040000")]
    [InlineData("<0305000400000000F")]
    [InlineData("<0305000400G00000")]
    [InlineData("<ZZ05000400000000")]
    [InlineData("<03-5000400000000")]
    [InlineData(">0305000400000000")]
    [InlineData("0305000400000000")]
    [InlineData("dbg: rescan done")]
    [InlineData("<+3050004000000000")]
    public void Rejects_malformed_lines_without_throwing(string? line)
    {
        Assert.False(VuResponse.TryParse(line, out var response));
        Assert.Null(response);
    }

    [Fact]
    public void Requests_round_trip_and_reject_a_wrong_length()
    {
        Assert.True(VuRequest.TryParse(">030400020132", out var request));
        Assert.Equal(VuCommand.SetDialPercentSingle, request.Command);
        Assert.Equal(VuDataType.KeyValuePair, request.DataType);
        Assert.Equal([0x01, 0x32], request.Payload.ToArray());
        Assert.Equal(">030400020132", request.Encode());

        Assert.False(VuRequest.TryParse(">0304000301", out _));
        Assert.False(VuRequest.TryParse("<030400020132", out _));
    }
}
