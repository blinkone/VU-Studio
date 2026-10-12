namespace VuStudio.Hardware.Protocol;

/// <summary>
/// Command codes of the VU1 hub (<c>CC</c> in a frame). Codes jump from <c>0x19</c> to <c>0x20</c>.
/// The bootloader range <c>0xF4</c>–<c>0xFD</c> is deliberately absent: VU Studio never flashes firmware.
/// </summary>
public enum VuCommand : byte
{
    /// <summary>Moves a needle by raw DAC value: <c>idx, hi, lo</c>.</summary>
    SetDialRawSingle = 0x01,

    /// <summary>Raw DAC values for several dials: <c>(idx, hi, lo) × n</c>.</summary>
    SetDialRawMultiple = 0x02,

    /// <summary>Needle position: <c>idx, percent</c> (0–100).</summary>
    SetDialPercentSingle = 0x03,

    /// <summary>Needle positions for several dials: <c>(idx, percent) × n</c>.</summary>
    SetDialPercentMultiple = 0x04,

    /// <summary>Stores the raw value for 100 %: <c>idx, u32</c>.</summary>
    SetDialCalibrateMax = 0x05,

    /// <summary>Stores the raw value for 50 %: <c>idx, u32</c>.</summary>
    SetDialCalibrateHalf = 0x06,

    /// <summary>Returns one byte per bus slot; <c>01</c> = present.</summary>
    GetDevicesMap = 0x07,

    /// <summary>Assigns bus addresses to new dials.</summary>
    ProvisionDevice = 0x08,

    ResetAllDevices = 0x09,

    /// <summary>Hub-wide dial power: <c>0</c> = off, <c>1</c> = on.</summary>
    DialPower = 0x0A,

    /// <summary>Returns the 12-byte UID of the dial at <c>idx</c>.</summary>
    GetDeviceUid = 0x0B,

    RescanBus = 0x0C,

    /// <summary>Fills the image buffer: <c>idx, color</c> (<c>0</c> = white, <c>1</c> = black).</summary>
    DisplayClear = 0x0D,

    /// <summary>Moves the image write cursor: <c>idx, x u16, y u16</c>.</summary>
    DisplayGotoXY = 0x0E,

    /// <summary>Writes image bytes at the cursor: <c>idx, bytes…</c> (at most 1000 per frame).</summary>
    DisplayImageData = 0x0F,

    /// <summary>Shows the image buffer (starts the e-paper refresh): <c>idx</c>.</summary>
    DisplayShowImage = 0x10,

    /// <summary>Returns the dial's receive buffer size (first 4 bytes, u32).</summary>
    RxBufferSize = 0x11,

    /// <summary>Not used by VU Studio.</summary>
    ResetConfig = 0x12,

    /// <summary>Backlight levels: <c>idx, r, g, b, w</c> (0–100 each).</summary>
    SetRgbBacklight = 0x13,

    /// <summary>Needle easing step (% per period): <c>idx, u32</c>.</summary>
    SetDialEasingStep = 0x14,

    /// <summary>Needle easing period (ms): <c>idx, u32</c>.</summary>
    SetDialEasingPeriod = 0x15,

    /// <summary>Backlight easing step (% per period): <c>idx, u32</c>.</summary>
    SetBacklightEasingStep = 0x16,

    /// <summary>Backlight easing period (ms): <c>idx, u32</c>.</summary>
    SetBacklightEasingPeriod = 0x17,

    /// <summary>Returns 16 bytes: needle step, needle period, backlight step, backlight period (u32 each).</summary>
    GetEasingConfig = 0x18,

    /// <summary>Returns the firmware build hash as ASCII.</summary>
    GetBuildInfo = 0x19,

    /// <summary>Returns the firmware version as ASCII. Note the gap after <see cref="GetBuildInfo"/>.</summary>
    GetFirmwareInfo = 0x20,

    /// <summary>Returns the hardware version as ASCII.</summary>
    GetHardwareInfo = 0x21,

    /// <summary>Returns the protocol version as ASCII.</summary>
    GetProtocolInfo = 0x22,

    /// <summary>Prints debug output.</summary>
    DebugI2CScan = 0xF3,
}
