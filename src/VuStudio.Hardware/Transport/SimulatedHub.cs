using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Hardware.Protocol;

namespace VuStudio.Hardware.Transport;

/// <summary>
/// An in-memory VU1 hub with a chain of dials that speaks the serial protocol line by line
/// (<c>docs/vu1-protocol.md</c>). It keeps per-dial state (needle, calibration, backlight, easing, image buffer
/// and displayed image) and supports fault injection. Used by <c>--simulate N</c>, tests and demos.
/// Connect to it with <see cref="SimulatedVuTransport"/>. Thread-safe.
/// </summary>
/// <remarks>
/// UIDs are deterministic: <c>"VU1SIM"</c> in ASCII, the 16-bit seed and the 32-bit dial number (1-based),
/// e.g. <c>56553153494D000000000001</c>. The device map reflects the last bus scan; unprovisioned dials appear
/// after <see cref="VuCommand.ProvisionDevice"/> followed by <see cref="VuCommand.RescanBus"/>.
/// </remarks>
public sealed class SimulatedHub
{
    /// <summary>Bus indices are one byte; the protocol documents 0–99.</summary>
    public const int MaxDials = 100;

    /// <summary>Number of recent commands kept for <see cref="GetCommandLog"/>.</summary>
    public const int CommandLogCapacity = 2048;

    public const uint DefaultCalibrationMax = 60000;

    public const uint DefaultCalibrationHalf = 30000;

    public const uint RxBufferSize = 2048;

    /// <summary>The device map always reports at least this many slots.</summary>
    private const int MinDeviceMapLength = 16;

    /// <summary>Info strings are NUL-padded to this many bytes, like fixed-size firmware buffers.</summary>
    private const int InfoFieldLength = 16;

    private readonly Lock _lock = new();
    private readonly List<Dial> _dials = [];
    private readonly List<SimulatedVuTransport> _attached = [];
    private readonly Queue<SimulatedCommand> _log = new();
    private readonly HashSet<int> _scanned = [];
    private TimeSpan _latency;
    private bool _emitDebugLines;
    private long _sequence;
    private int _failCount;
    private VuStatus _failStatus;
    private int _dropCount;
    private bool _pluggedIn = true;
    private bool _dialPower = true;

    /// <param name="dialCount">Dials on the bus, at indices 0…n-1.</param>
    /// <param name="uidSeed">Distinguishes the UIDs of several simulated hubs.</param>
    /// <param name="provisioned">False to start with dials that need <see cref="VuCommand.ProvisionDevice"/>.</param>
    public SimulatedHub(int dialCount, int uidSeed = 0, bool provisioned = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dialCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dialCount, MaxDials);
        for (var i = 0; i < dialCount; i++)
        {
            _dials.Add(new Dial(MakeUid(uidSeed, i), i) { Provisioned = provisioned });
        }

        ScanBus();
    }

    /// <summary>Raised after each command is processed, outside the hub's lock.</summary>
    public event Action<SimulatedCommand>? CommandProcessed;

    public string FirmwareVersion { get; init; } = "1.0.0-sim";

    public string HardwareVersion { get; init; } = "VU1-SIM rev.A";

    public string ProtocolVersion { get; init; } = "1";

    public string BuildHash { get; init; } = "5139a7e";

    public int DialCount => _dials.Count;

    /// <summary>Delay before replies reach the transport. Default 0.</summary>
    public TimeSpan Latency
    {
        get
        {
            lock (_lock)
            {
                return _latency;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            lock (_lock)
            {
                _latency = value;
            }
        }
    }

    /// <summary>When true, a debug line precedes every reply, as the real hub sometimes prints.</summary>
    public bool EmitDebugLines
    {
        get
        {
            lock (_lock)
            {
                return _emitDebugLines;
            }
        }

        set
        {
            lock (_lock)
            {
                _emitDebugLines = value;
            }
        }
    }

    public bool IsPluggedIn
    {
        get
        {
            lock (_lock)
            {
                return _pluggedIn;
            }
        }
    }

    /// <summary>State of <see cref="VuCommand.DialPower"/>; commands to dials fail while it is off.</summary>
    public bool DialPower
    {
        get
        {
            lock (_lock)
            {
                return _dialPower;
            }
        }
    }

    /// <summary>Number of commands received since creation.</summary>
    public long CommandCount
    {
        get
        {
            lock (_lock)
            {
                return _sequence;
            }
        }
    }

    /// <summary>Snapshots of all dials, ordered by bus index.</summary>
    public IReadOnlyList<SimulatedDialState> GetDials()
    {
        lock (_lock)
        {
            return [.. _dials.OrderBy(d => d.BusIndex).Select(d => d.Snapshot())];
        }
    }

    /// <exception cref="KeyNotFoundException">No dial has this UID.</exception>
    public SimulatedDialState GetDial(string uid)
    {
        lock (_lock)
        {
            return Find(uid).Snapshot();
        }
    }

    /// <summary>The most recent commands (up to <see cref="CommandLogCapacity"/>), oldest first.</summary>
    public IReadOnlyList<SimulatedCommand> GetCommandLog()
    {
        lock (_lock)
        {
            return [.. _log];
        }
    }

    /// <summary>The next <paramref name="count"/> commands are not executed and reply with <paramref name="status"/>.</summary>
    public void FailNextCommands(int count, VuStatus status = VuStatus.Fail)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_lock)
        {
            _failCount = count;
            _failStatus = status;
        }
    }

    /// <summary>The next <paramref name="count"/> commands are executed but their replies are lost.</summary>
    public void DropNextReplies(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_lock)
        {
            _dropCount = count;
        }
    }

    /// <summary>Every command addressed to the dial fails with <paramref name="status"/>; null makes it healthy again.</summary>
    public void SetDialFault(string uid, VuStatus? status)
    {
        lock (_lock)
        {
            Find(uid).Fault = status;
        }
    }

    /// <summary>Exchanges the bus indices of two dials, as a re-provisioned chain might.</summary>
    public void SwapBusIndices(string firstUid, string secondUid)
    {
        lock (_lock)
        {
            var first = Find(firstUid);
            var second = Find(secondUid);
            (first.BusIndex, second.BusIndex) = (second.BusIndex, first.BusIndex);
        }
    }

    /// <summary>
    /// Simulates pulling the USB cable: connected transports are lost, new ones cannot open, and the dials lose
    /// power (needle, backlight, easing and image buffer reset; the e-paper keeps its image).
    /// </summary>
    public void Unplug()
    {
        SimulatedVuTransport[] attached;
        lock (_lock)
        {
            if (!_pluggedIn)
            {
                return;
            }

            _pluggedIn = false;
            foreach (var dial in _dials)
            {
                dial.PowerLoss();
            }

            attached = [.. _attached];
            _attached.Clear();
        }

        foreach (var transport in attached)
        {
            transport.OnUnplugged();
        }
    }

    /// <summary>Plugs the hub back in; it boots with dial power on and scans the bus.</summary>
    public void PlugIn()
    {
        lock (_lock)
        {
            _pluggedIn = true;
            _dialPower = true;
            ScanBus();
        }
    }

    /// <summary>Handles one request line and returns the lines the hub prints in response.</summary>
    public IReadOnlyList<string> Process(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var replies = new List<string>();
        SimulatedCommand record;
        lock (_lock)
        {
            if (!_pluggedIn)
            {
                return replies;
            }

            var text = line.AsSpan().Trim();
            if (!VuProtocol.TryParseHeader(text, VuProtocol.RequestStart, out var code, out _, out var declared))
            {
                if (_emitDebugLines)
                {
                    replies.Add("dbg: ignored malformed request");
                }

                return replies;
            }

            var command = (VuCommand)code;
            VuStatus status;
            byte[]? data = null;
            if (!VuProtocol.TryDecodeHex(text[VuProtocol.HeaderLength..], out var payload) || payload.Length != declared)
            {
                payload = [];
                status = VuStatus.MalformedPackage;
            }
            else if (_failCount > 0)
            {
                _failCount--;
                status = _failStatus;
            }
            else
            {
                (status, data) = FaultFor(command, payload) is { } fault ? (fault, null) : Execute(command, payload);
            }

            var replied = _dropCount == 0;
            if (!replied)
            {
                _dropCount--;
            }

            record = new SimulatedCommand(++_sequence, command, AddressedIndex(command, payload), payload, status, replied);
            _log.Enqueue(record);
            if (_log.Count > CommandLogCapacity)
            {
                _log.Dequeue();
            }

            if (replied)
            {
                if (_emitDebugLines)
                {
                    replies.Add($"dbg: cmd {code:X2} len {payload.Length}");
                }

                if (command == VuCommand.DebugI2CScan && status == VuStatus.Ok)
                {
                    replies.AddRange(_dials.Where(d => d.Provisioned).OrderBy(d => d.BusIndex).Select(d => $"i2c: dial {d.BusIndex} at 0x{0x10 + d.BusIndex:X2}"));
                }

                replies.Add(data is not null && status == VuStatus.Ok
                    ? VuProtocol.EncodeResponse(command, VuDataType.MultipleValue, data)
                    : VuProtocol.EncodeStatusResponse(command, status));
            }
        }

        CommandProcessed?.Invoke(record);
        return replies;
    }

    internal bool Attach(SimulatedVuTransport transport)
    {
        lock (_lock)
        {
            if (!_pluggedIn)
            {
                return false;
            }

            _attached.Add(transport);
            return true;
        }
    }

    internal void Detach(SimulatedVuTransport transport)
    {
        lock (_lock)
        {
            _attached.Remove(transport);
        }
    }

    private (VuStatus Status, byte[]? Data) Execute(VuCommand command, byte[] p)
    {
        switch (command)
        {
            case VuCommand.SetDialRawSingle:
                return WithDial(p, 3, d => d.SetRaw(BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(1))));
            case VuCommand.SetDialRawMultiple:
                return ForEachDial(p, 3, (d, entry) => d.SetRaw(BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(1))));
            case VuCommand.SetDialPercentSingle:
                return p.Length == 2 && p[1] > 100 ? (VuStatus.BadData, null) : WithDial(p, 2, d => d.SetPercent(p[1]));
            case VuCommand.SetDialPercentMultiple:
                return ForEachDial(p, 2, (d, entry) => d.SetPercent(entry[1]), entry => entry[1] <= 100);
            case VuCommand.SetDialCalibrateMax:
                return WithDial(p, 5, d => d.CalibrationMax = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)));
            case VuCommand.SetDialCalibrateHalf:
                return WithDial(p, 5, d => d.CalibrationHalf = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)));
            case VuCommand.GetDevicesMap:
                return (VuStatus.Ok, DeviceMap());
            case VuCommand.ProvisionDevice:
                foreach (var dial in _dials)
                {
                    dial.Provisioned = true;
                }

                return (VuStatus.Ok, null);
            case VuCommand.ResetAllDevices:
                foreach (var dial in _dials)
                {
                    dial.PowerLoss();
                }

                return (VuStatus.Ok, null);
            case VuCommand.DialPower:
                if (p.Length != 1 || p[0] > 1)
                {
                    return (VuStatus.BadData, null);
                }

                _dialPower = p[0] == 1;
                return (VuStatus.Ok, null);
            case VuCommand.GetDeviceUid:
                return Query(p, d => d.UidBytes);
            case VuCommand.RescanBus:
                ScanBus();
                return (VuStatus.Ok, null);
            case VuCommand.DisplayClear:
                return p.Length == 2 && p[1] > 1 ? (VuStatus.BadData, null) : WithDial(p, 2, d => d.Clear(black: p[1] == 1));
            case VuCommand.DisplayGotoXY:
                return GotoXY(p);
            case VuCommand.DisplayImageData:
                return ImageData(p);
            case VuCommand.DisplayShowImage:
                return WithDial(p, 1, d => d.Show());
            case VuCommand.RxBufferSize:
                return Query(p, _ => U32(RxBufferSize));
            case VuCommand.ResetConfig:
                return (VuStatus.Ok, null);
            case VuCommand.SetRgbBacklight:
                return p.Length == 5 && p.Skip(1).Any(v => v > 100)
                    ? (VuStatus.BadData, null)
                    : WithDial(p, 5, d => d.Backlight = new LedLevels(p[1], p[2], p[3], p[4]));
            case VuCommand.SetDialEasingStep:
                return WithDial(p, 5, d => d.NeedleStep = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)));
            case VuCommand.SetDialEasingPeriod:
                return WithDial(p, 5, d => d.NeedlePeriod = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)));
            case VuCommand.SetBacklightEasingStep:
                return WithDial(p, 5, d => d.BacklightStep = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)));
            case VuCommand.SetBacklightEasingPeriod:
                return WithDial(p, 5, d => d.BacklightPeriod = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)));
            case VuCommand.GetEasingConfig:
                return Query(p, d => [.. U32(d.NeedleStep), .. U32(d.NeedlePeriod), .. U32(d.BacklightStep), .. U32(d.BacklightPeriod)]);
            case VuCommand.GetBuildInfo:
                return Query(p, _ => Text(BuildHash));
            case VuCommand.GetFirmwareInfo:
                return Query(p, _ => Text(FirmwareVersion));
            case VuCommand.GetHardwareInfo:
                return Query(p, _ => Text(HardwareVersion));
            case VuCommand.GetProtocolInfo:
                return Query(p, _ => Text(ProtocolVersion));
            case VuCommand.DebugI2CScan:
                return (VuStatus.Ok, null);
            default:
                return (VuStatus.Unsupported, null);
        }
    }

    private (VuStatus, byte[]?) GotoXY(byte[] p)
    {
        if (p.Length != 5)
        {
            return (VuStatus.BadData, null);
        }

        var x = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(1));
        var y = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(3));
        return x >= EInkEncoder.Width || y >= EInkEncoder.Height
            ? (VuStatus.BadData, null)
            : WithDial(p, 5, d => d.Cursor = (x * EInkEncoder.BytesPerColumn) + (y / 8));
    }

    private (VuStatus, byte[]?) ImageData(byte[] p)
    {
        if (p.Length < 2 || p.Length > VuProtocol.MaxImageChunk + 1)
        {
            return (VuStatus.BadData, null);
        }

        if (!TryGetDial(p[0], out var dial))
        {
            return (VuStatus.BadAddress, null);
        }

        if (!_dialPower)
        {
            return (VuStatus.DeviceOffline, null);
        }

        var data = p.AsSpan(1);
        if (dial.Cursor + data.Length > EInkEncoder.FrameLength)
        {
            return (VuStatus.BadData, null);
        }

        data.CopyTo(dial.Buffer.AsSpan(dial.Cursor));
        dial.Cursor += data.Length;
        return (VuStatus.Ok, null);
    }

    private (VuStatus, byte[]?) WithDial(byte[] p, int length, Action<Dial> action)
    {
        if (p.Length != length)
        {
            return (VuStatus.BadData, null);
        }

        if (!TryGetDial(p[0], out var dial))
        {
            return (VuStatus.BadAddress, null);
        }

        if (!_dialPower)
        {
            return (VuStatus.DeviceOffline, null);
        }

        action(dial);
        return (VuStatus.Ok, null);
    }

    private (VuStatus, byte[]?) ForEachDial(byte[] p, int stride, Action<Dial, byte[]> action, Func<byte[], bool>? isValid = null)
    {
        if (p.Length == 0 || p.Length % stride != 0)
        {
            return (VuStatus.BadData, null);
        }

        var entries = p.Chunk(stride).ToList();
        if (isValid is not null && !entries.All(isValid))
        {
            return (VuStatus.BadData, null);
        }

        var dials = new List<Dial>();
        foreach (var entry in entries)
        {
            if (!TryGetDial(entry[0], out var dial))
            {
                return (VuStatus.BadAddress, null);
            }

            dials.Add(dial);
        }

        if (!_dialPower)
        {
            return (VuStatus.DeviceOffline, null);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            action(dials[i], entries[i]);
        }

        return (VuStatus.Ok, null);
    }

    private (VuStatus, byte[]?) Query(byte[] p, Func<Dial, byte[]> query)
    {
        if (p.Length != 1)
        {
            return (VuStatus.BadData, null);
        }

        if (!TryGetDial(p[0], out var dial))
        {
            return (VuStatus.BadAddress, null);
        }

        return _dialPower ? (VuStatus.Ok, query(dial)) : (VuStatus.DeviceOffline, null);
    }

    private VuStatus? FaultFor(VuCommand command, byte[] p)
    {
        var stride = command switch
        {
            VuCommand.SetDialRawMultiple => 3,
            VuCommand.SetDialPercentMultiple => 2,
            _ => 0,
        };

        if (stride > 0)
        {
            return p.Chunk(stride).Select(entry => TryGetDial(entry[0], out var d) ? d.Fault : null).FirstOrDefault(fault => fault is not null);
        }

        return AddressedIndex(command, p) is { } index && TryGetDial(index, out var dial) ? dial.Fault : null;
    }

    private static int? AddressedIndex(VuCommand command, byte[] p) => command switch
    {
        VuCommand.SetDialRawSingle or VuCommand.SetDialPercentSingle or VuCommand.SetDialCalibrateMax or
            VuCommand.SetDialCalibrateHalf or VuCommand.GetDeviceUid or VuCommand.DisplayClear or VuCommand.DisplayGotoXY or
            VuCommand.DisplayImageData or VuCommand.DisplayShowImage or VuCommand.RxBufferSize or VuCommand.SetRgbBacklight or
            VuCommand.SetDialEasingStep or VuCommand.SetDialEasingPeriod or VuCommand.SetBacklightEasingStep or
            VuCommand.SetBacklightEasingPeriod or VuCommand.GetEasingConfig or VuCommand.GetBuildInfo or
            VuCommand.GetFirmwareInfo or VuCommand.GetHardwareInfo or VuCommand.GetProtocolInfo when p.Length > 0 => p[0],
        _ => null,
    };

    private bool TryGetDial(int busIndex, [NotNullWhen(true)] out Dial? dial)
    {
        dial = _dials.FirstOrDefault(d => d.Provisioned && d.BusIndex == busIndex);
        return dial is not null;
    }

    private Dial Find(string uid) =>
        _dials.FirstOrDefault(d => string.Equals(d.Uid, uid, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No simulated dial has UID '{uid}'.");

    private void ScanBus()
    {
        _scanned.Clear();
        foreach (var dial in _dials.Where(d => d.Provisioned))
        {
            _scanned.Add(dial.BusIndex);
        }
    }

    private byte[] DeviceMap()
    {
        var map = new byte[Math.Max(MinDeviceMapLength, _scanned.Count == 0 ? 0 : _scanned.Max() + 1)];
        foreach (var index in _scanned)
        {
            map[index] = 0x01;
        }

        return map;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Text(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        return bytes.Length >= InfoFieldLength ? bytes : [.. bytes, .. new byte[InfoFieldLength - bytes.Length]];
    }

    private static byte[] MakeUid(int seed, int index)
    {
        var uid = new byte[12];
        "VU1SIM"u8.CopyTo(uid);
        BinaryPrimitives.WriteUInt16BigEndian(uid.AsSpan(6), (ushort)seed);
        BinaryPrimitives.WriteUInt32BigEndian(uid.AsSpan(8), (uint)index + 1);
        return uid;
    }

    private sealed class Dial(byte[] uid, int busIndex)
    {
        public byte[] UidBytes { get; } = uid;

        public string Uid { get; } = Convert.ToHexString(uid);

        public int BusIndex { get; set; } = busIndex;

        public bool Provisioned { get; set; }

        public int Percent { get; private set; }

        public int Raw { get; private set; }

        public uint CalibrationMax { get; set; } = DefaultCalibrationMax;

        public uint CalibrationHalf { get; set; } = DefaultCalibrationHalf;

        public LedLevels Backlight { get; set; }

        public uint NeedleStep { get; set; } = (uint)Easing.DefaultNeedle.Step;

        public uint NeedlePeriod { get; set; } = (uint)Easing.DefaultNeedle.PeriodMs;

        public uint BacklightStep { get; set; } = (uint)Easing.DefaultBacklight.Step;

        public uint BacklightPeriod { get; set; } = (uint)Easing.DefaultBacklight.PeriodMs;

        public byte[] Buffer { get; } = White();

        public int Cursor { get; set; }

        public byte[] Displayed { get; } = White();

        public int RefreshCount { get; private set; }

        public VuStatus? Fault { get; set; }

        public void SetPercent(int percent)
        {
            Percent = percent;
            var half = (long)CalibrationHalf;
            var raw = percent <= 50
                ? half * percent / 50
                : half + (((long)CalibrationMax - half) * (percent - 50) / 50);
            Raw = (int)Math.Clamp(raw, 0, ushort.MaxValue);
        }

        public void SetRaw(int raw) => Raw = raw;

        public void Clear(bool black)
        {
            Array.Fill(Buffer, black ? (byte)0x00 : (byte)0xFF);
            Cursor = 0;
        }

        public void Show()
        {
            Buffer.CopyTo(Displayed, 0);
            RefreshCount++;
        }

        /// <summary>Volatile state resets; calibration, provisioning and the e-paper image survive.</summary>
        public void PowerLoss()
        {
            Percent = 0;
            Raw = 0;
            Backlight = LedLevels.Off;
            NeedleStep = (uint)Easing.DefaultNeedle.Step;
            NeedlePeriod = (uint)Easing.DefaultNeedle.PeriodMs;
            BacklightStep = (uint)Easing.DefaultBacklight.Step;
            BacklightPeriod = (uint)Easing.DefaultBacklight.PeriodMs;
            Array.Fill(Buffer, (byte)0xFF);
            Cursor = 0;
        }

        public SimulatedDialState Snapshot() => new()
        {
            Uid = Uid,
            BusIndex = BusIndex,
            Provisioned = Provisioned,
            Percent = Percent,
            RawValue = Raw,
            CalibrationMax = CalibrationMax,
            CalibrationHalf = CalibrationHalf,
            Backlight = Backlight,
            NeedleEasing = new Easing(ToInt(NeedleStep), ToInt(NeedlePeriod)),
            BacklightEasing = new Easing(ToInt(BacklightStep), ToInt(BacklightPeriod)),
            RefreshCount = RefreshCount,
            Fault = Fault,
            ImageBuffer = Buffer.ToArray(),
            DisplayedImage = Displayed.ToArray(),
        };

        private static int ToInt(uint value) => (int)Math.Min(value, int.MaxValue);

        private static byte[] White()
        {
            var frame = new byte[EInkEncoder.FrameLength];
            Array.Fill(frame, (byte)0xFF);
            return frame;
        }
    }
}
