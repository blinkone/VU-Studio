using System.Text;

namespace VuStudio.Sources.Windows.HwInfo;

/// <summary>Read access to HWiNFO's shared memory area. The Windows implementation wraps a mapped view and the mutex.</summary>
internal interface IHwInfoMemory : IDisposable
{
    /// <summary>Size of the mapped area in bytes (may be rounded up to a page).</summary>
    long Length { get; }

    /// <summary>Copies <paramref name="count"/> bytes starting at <paramref name="position"/> into <paramref name="destination"/>.</summary>
    void Read(long position, byte[] destination, int count);

    /// <summary>Takes HWiNFO's mutex for a short copy. Returns a no-op lease when it cannot be taken.</summary>
    IDisposable AcquireLock();
}

internal enum HwInfoReadStatus
{
    Ok,
    BadSignature,
    Dead,
    BadLayout,

    /// <summary>The area kept changing while it was copied (HWiNFO was republishing).</summary>
    Inconsistent,
}

internal readonly record struct HwInfoReadResult(HwInfoReadStatus Status, HwInfoSnapshot? Snapshot);

/// <summary>
/// Torn-read-safe reader: header, bulk copy, header again; a mismatch means HWiNFO republished while we copied, so
/// retry. Parsing only ever touches the private copy, never the live view.
/// </summary>
internal sealed class HwInfoReader(Encoding ansi, int maxAttempts = 5, Action<int>? backoff = null)
{
    private byte[] _buffer = new byte[256 * 1024];
    private readonly byte[] _headerBuffer = new byte[HwInfoLayout.HeaderSize];

    public HwInfoReadResult Read(IHwInfoMemory memory)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                (backoff ?? DefaultBackoff)(attempt);
            }

            using (memory.AcquireLock())
            {
                var first = ReadHeader(memory);
                if (first is null)
                {
                    return new(HwInfoReadStatus.BadLayout, null);
                }

                var status = HwInfoParser.ValidateHeader(first.Value, memory.Length);
                if (status != HwInfoParseStatus.Ok)
                {
                    return new(Map(status), null);
                }

                var area = (int)first.Value.AreaSize;
                if (_buffer.Length < area)
                {
                    _buffer = new byte[area];
                }

                memory.Read(0, _buffer, area);
                var second = ReadHeader(memory);
                if (second != first || !HwInfoHeader.TryParse(_buffer, out var copied) || copied != first)
                {
                    continue;
                }

                var parsed = HwInfoParser.Parse(_buffer.AsSpan(0, area), ansi);
                return parsed.Status == HwInfoParseStatus.Ok
                    ? new(HwInfoReadStatus.Ok, parsed.Snapshot)
                    : new(Map(parsed.Status), null);
            }
        }

        return new(HwInfoReadStatus.Inconsistent, null);
    }

    private HwInfoHeader? ReadHeader(IHwInfoMemory memory)
    {
        var count = (int)Math.Min(HwInfoLayout.HeaderSize, memory.Length);
        if (count < HwInfoLayout.HeaderSizeRevision0)
        {
            return null;
        }

        memory.Read(0, _headerBuffer, count);
        return HwInfoHeader.TryParse(_headerBuffer.AsSpan(0, count), out var header) ? header : null;
    }

    private static HwInfoReadStatus Map(HwInfoParseStatus status) => status switch
    {
        HwInfoParseStatus.Ok => HwInfoReadStatus.Ok,
        HwInfoParseStatus.Dead => HwInfoReadStatus.Dead,
        HwInfoParseStatus.BadSignature => HwInfoReadStatus.BadSignature,
        _ => HwInfoReadStatus.BadLayout,
    };

    private static void DefaultBackoff(int attempt) => Thread.Sleep(Math.Min(20 * attempt, 100));
}
