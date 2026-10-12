using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Imaging;

/// <summary>
/// Writes a <see cref="MonoBitmap"/> as a 2-color indexed PNG (1 bit per pixel). Upscaling is
/// nearest-neighbour; repeated rows use the "Up" filter so they compress to almost nothing.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="ink">Color of black pixels.</param>
    /// <param name="paper">Color of white pixels.</param>
    public static byte[] Encode(MonoBitmap bitmap, int scale, (byte R, byte G, byte B) ink, (byte R, byte G, byte B) paper)
    {
        scale = Math.Clamp(scale, 1, 32);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;
        var rowBytes = (width + 7) / 8;

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 1; // bit depth
        header[9] = 3; // indexed color
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "PLTE", [ink.R, ink.G, ink.B, paper.R, paper.G, paper.B]);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var row = new byte[rowBytes + 1];
                var upRow = new byte[rowBytes + 1];
                upRow[0] = 2; // "Up" filter: identical to the row above → all zeros
                var pixels = bitmap.Pixels;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    Array.Clear(row);
                    for (var x = 0; x < width; x++)
                    {
                        if (pixels[(y * bitmap.Width) + (x / scale)])
                        {
                            row[1 + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        }
                    }

                    zlib.Write(row);
                    for (var r = 1; r < scale; r++)
                    {
                        zlib.Write(upRow);
                    }
                }
            }

            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
