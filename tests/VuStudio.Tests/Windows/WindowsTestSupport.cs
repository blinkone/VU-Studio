using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using VuStudio.Sources.Windows.HwInfo;

namespace VuStudio.Tests.Windows;

/// <summary>
/// LibreHardwareMonitorLib only ships under <c>runtimes/win-*</c>, so a Linux test host cannot resolve it on its
/// own. The enums and interfaces the tests touch are plain managed types, so load the win-x64 copy on demand.
/// </summary>
internal static class LhmAssemblyResolver
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name != "LibreHardwareMonitorLib")
            {
                return null;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "lib", "net10.0", "LibreHardwareMonitorLib.dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}

internal static class IconSet
{
    private static readonly Lazy<HashSet<string>> Ids = new(Load);

    public static bool Contains(string id) => Ids.Value.Contains(id);

    private static HashSet<string> Load()
    {
        var assembly = Assembly.Load("VuStudio.Rendering");
        using var stream = assembly.GetManifestResourceStream("VuStudio.Rendering.Assets/Icons/icons.json")
            ?? throw new InvalidOperationException("icons.json is not embedded in VuStudio.Rendering");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("icons").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// Builds HWiNFO shared-memory buffers from the layout documented in docs/integrations/windows-sources.md §1.2.
/// The offsets are written as literals on purpose: they are an independent check of the parser's constants.
/// </summary>
internal static class SynthBuffer
{
    internal sealed record Sensor(uint Id, uint Instance, string Orig, string? User = null, string? Utf8 = null);

    internal sealed record Reading(
        int Type, uint SensorIndex, uint Id, string LabelOrig, string Unit, double Value,
        string? LabelUser = null, string? LabelUtf8 = null, string? UnitUtf8 = null);

    public static byte[] Build(
        int revision,
        bool utf8,
        long pollTime,
        uint pollingPeriod,
        Sensor[] sensors,
        Reading[] readings,
        uint signature = 0x53695748,
        int extraSensorStride = 0,
        int extraReadingStride = 0)
    {
        var headerSize = revision >= 1 ? 48 : 44;
        var sensorStride = (utf8 ? 392 : 264) + extraSensorStride;
        var readingStride = (utf8 ? 460 : 316) + extraReadingStride;
        var sensorOffset = headerSize;
        var readingOffset = sensorOffset + (sensors.Length * sensorStride);
        var buffer = new byte[readingOffset + (readings.Length * readingStride)];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], signature);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], utf8 ? 2u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)revision);
        BinaryPrimitives.WriteInt64LittleEndian(span[12..], pollTime);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)sensorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sensorStride);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)sensors.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], (uint)readingOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], (uint)readingStride);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)readings.Length);
        if (revision >= 1)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[44..], pollingPeriod);
        }

        for (var i = 0; i < sensors.Length; i++)
        {
            var e = span[(sensorOffset + (i * sensorStride))..];
            BinaryPrimitives.WriteUInt32LittleEndian(e[0..], sensors[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], sensors[i].Instance);
            Put(e[8..], 128, sensors[i].Orig, Encoding.Latin1);
            Put(e[136..], 128, sensors[i].User ?? sensors[i].Orig, Encoding.Latin1);
            if (utf8)
            {
                Put(e[264..], 128, sensors[i].Utf8 ?? sensors[i].User ?? sensors[i].Orig, Encoding.UTF8);
            }
        }

        for (var i = 0; i < readings.Length; i++)
        {
            var r = readings[i];
            var e = span[(readingOffset + (i * readingStride))..];
            BinaryPrimitives.WriteInt32LittleEndian(e[0..], r.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], r.SensorIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], r.Id);
            Put(e[12..], 128, r.LabelOrig, Encoding.Latin1);
            Put(e[140..], 128, r.LabelUser ?? r.LabelOrig, Encoding.Latin1);
            Put(e[268..], 16, r.Unit, Encoding.Latin1);
            BinaryPrimitives.WriteDoubleLittleEndian(e[284..], r.Value);
            BinaryPrimitives.WriteDoubleLittleEndian(e[292..], r.Value - 1);
            BinaryPrimitives.WriteDoubleLittleEndian(e[300..], r.Value + 1);
            BinaryPrimitives.WriteDoubleLittleEndian(e[308..], r.Value);
            if (utf8)
            {
                Put(e[316..], 128, r.LabelUtf8 ?? r.LabelUser ?? r.LabelOrig, Encoding.UTF8);
                Put(e[444..], 16, r.UnitUtf8 ?? r.Unit, Encoding.UTF8);
            }
        }

        return buffer;
    }

    private static void Put(Span<byte> destination, int length, string text, Encoding encoding)
    {
        var bytes = encoding.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, length)).CopyTo(destination);
    }
}

/// <summary>An in-memory stand-in for HWiNFO's shared memory that can change between reads.</summary>
internal sealed class FakeHwInfoMemory(byte[] data) : IHwInfoMemory
{
    public byte[] Data { get; set; } = data;

    /// <summary>Called before every read with the 1-based read number; may mutate <see cref="Data"/>.</summary>
    public Action<int, FakeHwInfoMemory>? OnRead { get; set; }

    public int Reads { get; private set; }

    public int LocksTaken { get; private set; }

    public bool Disposed { get; private set; }

    public long Length => Data.Length;

    public void Read(long position, byte[] destination, int count)
    {
        Reads++;
        OnRead?.Invoke(Reads, this);
        Array.Copy(Data, position, destination, 0, count);
    }

    public IDisposable AcquireLock()
    {
        LocksTaken++;
        return new Lock();
    }

    public void Dispose() => Disposed = true;

    private sealed class Lock : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
