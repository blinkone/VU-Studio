using System.Security.Cryptography;
using System.Text.Json;
using VuStudio.Core.Config;
using VuStudio.Core.Faces;

namespace VuStudio.Server.Storage;

/// <summary>Uploaded images stored as <c>{id}.bin</c> plus <c>{id}.json</c> metadata in the assets folder.</summary>
public sealed class FileAssetStore(string directory) : IAssetStore
{
    public const int MaxBytes = 5 * 1024 * 1024;

    private sealed record Meta(string? Name, long Size, DateTimeOffset Created);

    public byte[]? Get(string assetId)
    {
        var path = DataPath(assetId);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public string Add(ReadOnlySpan<byte> content, string? originalName)
    {
        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new ArgumentException($"Images must be between 1 byte and {MaxBytes / 1024 / 1024} MB.");
        }

        if (!LooksLikeImage(content))
        {
            throw new ArgumentException("Unsupported image format (use PNG, JPEG, GIF, BMP or WebP).");
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, id + ".bin"), content.ToArray());
        var name = originalName is null ? null : Path.GetFileName(originalName);
        File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(new Meta(name, content.Length, DateTimeOffset.UtcNow), Json.Compact));
        return id;
    }

    public IReadOnlyList<AssetInfo> List()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var list = new List<AssetInfo>();
        foreach (var metaPath in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), Json.Compact);
                if (meta is not null)
                {
                    list.Add(new AssetInfo(Path.GetFileNameWithoutExtension(metaPath), meta.Name, meta.Size, meta.Created));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // skip unreadable entries
            }
        }

        return list.OrderByDescending(a => a.Created).ToList();
    }

    public bool Delete(string assetId)
    {
        var path = DataPath(assetId);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        File.Delete(Path.ChangeExtension(path, ".json"));
        return true;
    }

    private string? DataPath(string assetId) =>
        assetId.Length is > 0 and <= 32 && assetId.All(char.IsAsciiLetterOrDigit)
            ? Path.Combine(directory, assetId.ToLowerInvariant() + ".bin")
            : null;

    private static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
        (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) || // PNG
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) || // JPEG
        (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') || // GIF
        (b.Length > 2 && b[0] == 'B' && b[1] == 'M') || // BMP
        (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P'); // WebP
}
