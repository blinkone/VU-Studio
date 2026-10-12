using System.Diagnostics.CodeAnalysis;

namespace VuStudio.Hardware.Protocol;

/// <summary>A host → hub frame: <c>&gt;CCTTLLLL&lt;hex&gt;</c>.</summary>
public sealed class VuRequest
{
    public VuRequest(VuCommand command, VuDataType dataType, ReadOnlyMemory<byte> payload)
    {
        Command = command;
        DataType = dataType;
        Payload = payload;
    }

    public VuCommand Command { get; }

    public VuDataType DataType { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The request line without terminator.</summary>
    public string Encode() => VuProtocol.EncodeRequest(Command, DataType, Payload.Span);

    /// <summary>
    /// Parses a request line strictly: valid header, valid hex payload and a declared length that matches it.
    /// Never throws.
    /// </summary>
    public static bool TryParse(string? line, [NotNullWhen(true)] out VuRequest? request)
    {
        request = null;
        if (line is null)
        {
            return false;
        }

        var text = line.AsSpan().Trim();
        if (!VuProtocol.TryParseHeader(text, VuProtocol.RequestStart, out var command, out var type, out var declared) ||
            !VuProtocol.TryDecodeHex(text[VuProtocol.HeaderLength..], out var payload) ||
            payload.Length != declared)
        {
            return false;
        }

        request = new VuRequest((VuCommand)command, (VuDataType)type, payload);
        return true;
    }

    public override string ToString() => $">{(byte)Command:X2}{(byte)DataType:X2}{Payload.Length:X4}{Convert.ToHexString(Payload.Span)}";
}
