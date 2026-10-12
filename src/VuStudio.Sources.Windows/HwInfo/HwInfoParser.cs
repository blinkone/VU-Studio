using System.Buffers.Binary;
using System.Text;

namespace VuStudio.Sources.Windows.HwInfo;

/// <summary>Layout constants of HWiNFO's shared memory (pack(1), little endian). See docs/integrations/windows-sources.md §1.</summary>
internal static class HwInfoLayout
{
    public const string MappingName = @"Global\HWiNFO_SENS_SM2";
    public const string MutexName = @"Global\HWiNFO_SM2_MUTEX";

    /// <summary>Bytes <c>H W i S</c> read as a little-endian uint32.</summary>
    public const uint LiveSignature = 0x53695748;

    /// <summary>ASCII "DEAD" in either byte order (the order is unverified).</summary>
    public const uint DeadSignatureA = 0x44414544;
    public const uint DeadSignatureB = 0x44454144;

    public const int HeaderSizeRevision0 = 44;
    public const int HeaderSize = 48;

    public const int MinSensorStride = 264;
    public const int MinReadingStride = 316;
    public const int Utf8SensorStride = 392;
    public const int Utf8ReadingStride = 460;

    public const int StringLength = 128;
    public const int UnitLength = 16;

    // Sensor element
    public const int SensorId = 0, SensorInstance = 4, SensorNameOrig = 8, SensorNameUser = 136, SensorNameUtf8 = 264;

    // Reading element
    public const int ReadingType = 0, ReadingSensorIndex = 4, ReadingId = 8, ReadingLabelOrig = 12, ReadingLabelUser = 140,
        ReadingUnit = 268, ReadingValue = 284, ReadingMin = 292, ReadingMax = 300, ReadingAvg = 308,
        ReadingLabelUtf8 = 316, ReadingUnitUtf8 = 444;

    /// <summary>Upper bound for the area we are willing to copy; real snapshots are about 230 KB.</summary>
    public const long MaxAreaBytes = 64L * 1024 * 1024;
}

internal enum HwInfoReadingType
{
    None = 0,
    Temp = 1,
    Volt = 2,
    Fan = 3,
    Current = 4,
    Power = 5,
    Clock = 6,
    Usage = 7,
    Other = 8,
}

internal readonly record struct HwInfoHeader(
    uint Signature,
    uint Version,
    uint Revision,
    long PollTime,
    uint SensorOffset,
    uint SensorStride,
    uint SensorCount,
    uint ReadingOffset,
    uint ReadingStride,
    uint ReadingCount,
    uint PollingPeriodMs)
{
    public bool IsLive => Signature == HwInfoLayout.LiveSignature;

    public bool IsDead => Signature is HwInfoLayout.DeadSignatureA or HwInfoLayout.DeadSignatureB;

    /// <summary>Bytes needed to hold both sections.</summary>
    public long AreaSize => Math.Max(
        (long)SensorOffset + ((long)SensorStride * SensorCount),
        (long)ReadingOffset + ((long)ReadingStride * ReadingCount));

    /// <summary>Reads the fixed header. Fails when the buffer is too short for the revision it announces.</summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out HwInfoHeader header)
    {
        header = default;
        if (buffer.Length < HwInfoLayout.HeaderSizeRevision0)
        {
            return false;
        }

        var revision = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]);
        if (revision >= 1 && buffer.Length < HwInfoLayout.HeaderSize)
        {
            return false;
        }

        header = new HwInfoHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            revision,
            BinaryPrimitives.ReadInt64LittleEndian(buffer[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[36..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[40..]),
            revision >= 1 ? BinaryPrimitives.ReadUInt32LittleEndian(buffer[44..]) : 0);
        return true;
    }
}

internal sealed record HwInfoSensor(uint Id, uint Instance, string Name, string OriginalName);

internal sealed record HwInfoReading(
    HwInfoReadingType Type,
    uint SensorIndex,
    uint ReadingId,
    string Label,
    string OriginalLabel,
    string Unit,
    double Value,
    double Min,
    double Max,
    double Average);

internal sealed record HwInfoSnapshot(HwInfoHeader Header, HwInfoSensor[] Sensors, HwInfoReading[] Readings);

internal enum HwInfoParseStatus
{
    Ok,
    TooShort,
    BadSignature,
    Dead,
    BadLayout,
}

internal readonly record struct HwInfoParseResult(HwInfoParseStatus Status, HwInfoHeader Header, HwInfoSnapshot? Snapshot);

/// <summary>Pure parser over a copy of the shared memory area. Never reads outside <c>buffer</c>.</summary>
internal static class HwInfoParser
{
    /// <summary>
    /// Validates the header and the section bounds only (no strings decoded). Used by the reader before it copies
    /// the whole area and by <see cref="Parse"/>.
    /// </summary>
    public static HwInfoParseStatus ValidateHeader(in HwInfoHeader h, long availableBytes)
    {
        if (h.IsDead)
        {
            return HwInfoParseStatus.Dead;
        }

        if (!h.IsLive)
        {
            return HwInfoParseStatus.BadSignature;
        }

        var minHeader = h.Revision >= 1 ? HwInfoLayout.HeaderSize : HwInfoLayout.HeaderSizeRevision0;
        if (h.SensorStride < HwInfoLayout.MinSensorStride
            || h.ReadingStride < HwInfoLayout.MinReadingStride
            || h.SensorOffset < minHeader
            || h.ReadingOffset < minHeader)
        {
            return HwInfoParseStatus.BadLayout;
        }

        var area = h.AreaSize;
        return area > HwInfoLayout.MaxAreaBytes || area > availableBytes ? HwInfoParseStatus.BadLayout : HwInfoParseStatus.Ok;
    }

    /// <param name="buffer">A copy of the area, starting at the header.</param>
    /// <param name="ansi">Encoding for the ANSI string fields (the system code page on Windows).</param>
    public static HwInfoParseResult Parse(ReadOnlySpan<byte> buffer, Encoding ansi)
    {
        if (!HwInfoHeader.TryParse(buffer, out var header))
        {
            return new(HwInfoParseStatus.TooShort, default, null);
        }

        var status = ValidateHeader(header, buffer.Length);
        if (status != HwInfoParseStatus.Ok)
        {
            return new(status, header, null);
        }

        var sensors = new HwInfoSensor[header.SensorCount];
        var hasUtf8Sensor = header.SensorStride >= HwInfoLayout.Utf8SensorStride;
        for (var i = 0; i < sensors.Length; i++)
        {
            var e = buffer.Slice((int)(header.SensorOffset + ((long)i * header.SensorStride)), (int)header.SensorStride);
            var orig = Text(e.Slice(HwInfoLayout.SensorNameOrig, HwInfoLayout.StringLength), ansi);
            var user = Text(e.Slice(HwInfoLayout.SensorNameUser, HwInfoLayout.StringLength), ansi);
            var utf8 = hasUtf8Sensor ? Text(e.Slice(HwInfoLayout.SensorNameUtf8, HwInfoLayout.StringLength), Encoding.UTF8) : "";
            sensors[i] = new HwInfoSensor(
                BinaryPrimitives.ReadUInt32LittleEndian(e[HwInfoLayout.SensorId..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[HwInfoLayout.SensorInstance..]),
                First(utf8, user, orig),
                orig);
        }

        var readings = new HwInfoReading[header.ReadingCount];
        var hasUtf8Reading = header.ReadingStride >= HwInfoLayout.Utf8ReadingStride;
        for (var i = 0; i < readings.Length; i++)
        {
            var e = buffer.Slice((int)(header.ReadingOffset + ((long)i * header.ReadingStride)), (int)header.ReadingStride);
            var rawType = BinaryPrimitives.ReadInt32LittleEndian(e[HwInfoLayout.ReadingType..]);
            var type = rawType is >= 0 and <= 8 ? (HwInfoReadingType)rawType : HwInfoReadingType.Other;
            var labelOrig = Text(e.Slice(HwInfoLayout.ReadingLabelOrig, HwInfoLayout.StringLength), ansi);
            var labelUser = Text(e.Slice(HwInfoLayout.ReadingLabelUser, HwInfoLayout.StringLength), ansi);
            var labelUtf8 = hasUtf8Reading ? Text(e.Slice(HwInfoLayout.ReadingLabelUtf8, HwInfoLayout.StringLength), Encoding.UTF8) : "";
            var unitAnsi = Text(e.Slice(HwInfoLayout.ReadingUnit, HwInfoLayout.UnitLength), ansi);
            var unitUtf8 = hasUtf8Reading ? Text(e.Slice(HwInfoLayout.ReadingUnitUtf8, HwInfoLayout.UnitLength), Encoding.UTF8) : "";
            readings[i] = new HwInfoReading(
                type,
                BinaryPrimitives.ReadUInt32LittleEndian(e[HwInfoLayout.ReadingSensorIndex..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[HwInfoLayout.ReadingId..]),
                First(labelUtf8, labelUser, labelOrig),
                labelOrig,
                First(unitUtf8, unitAnsi),
                BinaryPrimitives.ReadDoubleLittleEndian(e[HwInfoLayout.ReadingValue..]),
                BinaryPrimitives.ReadDoubleLittleEndian(e[HwInfoLayout.ReadingMin..]),
                BinaryPrimitives.ReadDoubleLittleEndian(e[HwInfoLayout.ReadingMax..]),
                BinaryPrimitives.ReadDoubleLittleEndian(e[HwInfoLayout.ReadingAvg..]));
        }

        return new(HwInfoParseStatus.Ok, header, new HwInfoSnapshot(header, sensors, readings));
    }

    /// <summary>Decodes a fixed-size string field: stops at the first NUL, never trusts a terminator to exist.</summary>
    public static string Text(ReadOnlySpan<byte> field, Encoding encoding)
    {
        var end = field.IndexOf((byte)0);
        if (end >= 0)
        {
            field = field[..end];
        }

        return field.IsEmpty ? "" : encoding.GetString(field).Trim();
    }

    private static string First(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (c.Length > 0)
            {
                return c;
            }
        }

        return "";
    }
}
