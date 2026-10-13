namespace VuStudio.Hardware.Protocol;

/// <summary>Payload type of a frame (<c>TT</c>).</summary>
public enum VuDataType : byte
{
    None = 0x01,
    SingleValue = 0x02,
    MultipleValue = 0x03,
    KeyValuePair = 0x04,

    /// <summary>Response payload is a 4-byte big-endian <see cref="VuStatus"/>.</summary>
    StatusCode = 0x05,
}
