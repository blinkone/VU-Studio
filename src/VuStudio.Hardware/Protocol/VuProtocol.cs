using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;

namespace VuStudio.Hardware.Protocol;

/// <summary>
/// Framing of the VU1 hub protocol (see <c>docs/vu1-protocol.md</c>): <c>&gt;CCTTLLLL&lt;hex&gt;</c> requests and
/// <c>&lt;CCTTLLLL&lt;hex&gt;</c> responses, uppercase hex, without the <c>\r\n</c> terminator (added by the transport).
/// </summary>
public static class VuProtocol
{
    public const char RequestStart = '>';

    public const char ResponseStart = '<';

    /// <summary>Length of the start character plus <c>CCTTLLLL</c>.</summary>
    public const int HeaderLength = 9;

    /// <summary>Largest payload a frame can declare (<c>LLLL</c> is four hex digits).</summary>
    public const int MaxPayloadLength = 0xFFFF;

    /// <summary>Largest number of image bytes in one <see cref="VuCommand.DisplayImageData"/> frame.</summary>
    public const int MaxImageChunk = 1000;

    /// <summary>The data type VU-Server sends with each command; the hub does not validate it strictly.</summary>
    public static VuDataType DefaultDataType(VuCommand command) => command switch
    {
        VuCommand.SetDialRawSingle or VuCommand.SetDialRawMultiple or VuCommand.SetDialPercentSingle or
            VuCommand.SetDialPercentMultiple or VuCommand.SetDialCalibrateMax or VuCommand.SetDialCalibrateHalf => VuDataType.KeyValuePair,
        VuCommand.GetDevicesMap or VuCommand.ProvisionDevice or VuCommand.ResetAllDevices or VuCommand.RescanBus or
            VuCommand.ResetConfig or VuCommand.DebugI2CScan => VuDataType.None,
        VuCommand.SetRgbBacklight => VuDataType.MultipleValue,
        _ => VuDataType.SingleValue,
    };

    /// <summary>True for the bootloader / firmware-update range (<c>0xF4</c>–<c>0xFD</c>), which VU Studio never sends.</summary>
    public static bool IsBootloaderCommand(byte code) => code is >= 0xF4 and <= 0xFD;

    /// <summary>Encodes a request with the command's <see cref="DefaultDataType"/>.</summary>
    public static string EncodeRequest(VuCommand command, ReadOnlySpan<byte> payload) =>
        EncodeRequest(command, DefaultDataType(command), payload);

    /// <summary>Encodes a request line, e.g. <c>&gt;030400020132</c> for "dial 1 to 50 %".</summary>
    /// <exception cref="ArgumentException">Bootloader command, or payload longer than <see cref="MaxPayloadLength"/>.</exception>
    public static string EncodeRequest(VuCommand command, VuDataType type, ReadOnlySpan<byte> payload)
    {
        if (IsBootloaderCommand((byte)command))
        {
            throw new ArgumentException($"Bootloader command 0x{(byte)command:X2} is not supported.", nameof(command));
        }

        return Encode(RequestStart, (byte)command, (byte)type, payload);
    }

    /// <summary>Encodes a response line as the hub would send it (used by the simulator).</summary>
    public static string EncodeResponse(VuCommand command, VuDataType type, ReadOnlySpan<byte> payload) =>
        Encode(ResponseStart, (byte)command, (byte)type, payload);

    /// <summary>Encodes a status reply, e.g. <c>&lt;0305000400000000</c>.</summary>
    public static string EncodeStatusResponse(VuCommand command, VuStatus status)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)status);
        return EncodeResponse(command, VuDataType.StatusCode, payload);
    }

    private static string Encode(char start, byte command, byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException($"Payload exceeds {MaxPayloadLength} bytes.", nameof(payload));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{start}{command:X2}{type:X2}{payload.Length:X4}{Convert.ToHexString(payload)}");
    }

    /// <summary>Parses <c>sCCTTLLLL</c> without throwing; <paramref name="length"/> is the declared (advisory) length.</summary>
    internal static bool TryParseHeader(ReadOnlySpan<char> line, char start, out byte command, out byte type, out int length)
    {
        command = 0;
        type = 0;
        length = 0;
        if (line.Length < HeaderLength || line[0] != start)
        {
            return false;
        }

        if (!TryParseHex(line.Slice(1, 2), out var cc) || !TryParseHex(line.Slice(3, 2), out var tt) || !TryParseHex(line.Slice(5, 4), out var ll))
        {
            return false;
        }

        command = (byte)cc;
        type = (byte)tt;
        length = ll;
        return true;
    }

    /// <summary>Decodes an even-length hex string (either case) without throwing.</summary>
    internal static bool TryDecodeHex(ReadOnlySpan<char> hex, out byte[] bytes)
    {
        bytes = [];
        if (hex.Length % 2 != 0)
        {
            return false;
        }

        var buffer = new byte[hex.Length / 2];
        if (Convert.FromHexString(hex, buffer, out _, out var written) != OperationStatus.Done || written != buffer.Length)
        {
            return false;
        }

        bytes = buffer;
        return true;
    }

    private static bool TryParseHex(ReadOnlySpan<char> digits, out int value) =>
        int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
}
