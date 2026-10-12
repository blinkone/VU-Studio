namespace VuStudio.Hardware.Protocol;

/// <summary>Status returned by the hub in <see cref="VuDataType.StatusCode"/> replies (u32, big-endian).</summary>
public enum VuStatus : uint
{
    Ok = 0x0000,
    Fail = 0x0001,
    Busy = 0x0002,
    Timeout = 0x0003,
    BadData = 0x0004,
    ProtocolError = 0x0005,
    NoMemory = 0x0006,
    InvalidArgument = 0x0007,
    BadAddress = 0x0008,
    Forbidden = 0x0009,
    AlreadyExists = 0x000B,
    Unsupported = 0x000C,
    NotImplemented = 0x000D,
    MalformedPackage = 0x000E,
    RecursiveCall = 0x0010,
    DataMismatch = 0x0011,
    DeviceOffline = 0x0012,
    ModuleNotInitialized = 0x0013,
    I2CError = 0x0014,
    UsartError = 0x0015,
    SpiError = 0x0016,
    BootloaderNoDevice = 0xE001,
    BootloaderInvalidState = 0xE002,
    BootloaderInvalidRequest = 0xE003,
}
