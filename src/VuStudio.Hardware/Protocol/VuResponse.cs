using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace VuStudio.Hardware.Protocol;

/// <summary>A decoded hub → host line: <c>&lt;CCTTLLLL&lt;hex&gt;</c>.</summary>
public sealed class VuResponse
{
    public VuResponse(VuCommand command, VuDataType dataType, int declaredLength, ReadOnlyMemory<byte> payload)
    {
        if (dataType == VuDataType.StatusCode && payload.Length < 4)
        {
            throw new ArgumentException("A status reply carries at least 4 bytes.", nameof(payload));
        }

        Command = command;
        DataType = dataType;
        DeclaredLength = declaredLength;
        Payload = payload;
    }

    /// <summary>The echoed command code; may be a value outside <see cref="VuCommand"/>.</summary>
    public VuCommand Command { get; }

    public VuDataType DataType { get; }

    /// <summary>The <c>LLLL</c> field. Advisory only: <see cref="Payload"/> is everything after the header.</summary>
    public int DeclaredLength { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public bool IsStatus => DataType == VuDataType.StatusCode;

    /// <summary>The status of a status reply; null for data replies.</summary>
    public VuStatus? Status => IsStatus ? (VuStatus)BinaryPrimitives.ReadUInt32BigEndian(Payload.Span) : null;

    /// <summary>
    /// Parses a response line. Never throws: lines that are not well-formed responses (debug output, noise,
    /// truncated frames, bad hex, status replies shorter than 4 bytes) return false.
    /// Surrounding whitespace, including a trailing <c>\r\n</c>, is ignored.
    /// </summary>
    public static bool TryParse(string? line, [NotNullWhen(true)] out VuResponse? response)
    {
        response = null;
        if (line is null)
        {
            return false;
        }

        var text = line.AsSpan().Trim();
        if (!VuProtocol.TryParseHeader(text, VuProtocol.ResponseStart, out var command, out var type, out var declared) ||
            !VuProtocol.TryDecodeHex(text[VuProtocol.HeaderLength..], out var payload) ||
            (type == (byte)VuDataType.StatusCode && payload.Length < 4))
        {
            return false;
        }

        response = new VuResponse((VuCommand)command, (VuDataType)type, declared, payload);
        return true;
    }

    public override string ToString() =>
        $"<{(byte)Command:X2}{(byte)DataType:X2}{DeclaredLength:X4}{Convert.ToHexString(Payload.Span)}";
}
