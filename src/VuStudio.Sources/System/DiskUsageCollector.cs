using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics;

/// <summary>A mounted fixed drive.</summary>
/// <param name="Name">Drive letter root (<c>C:\</c>) or mount point (<c>/mnt/disk1</c>).</param>
/// <param name="Label">Volume label, if any.</param>
/// <param name="TotalBytes">Capacity.</param>
/// <param name="FreeBytes">Space not in use, including what is reserved for the superuser.</param>
/// <param name="AvailableBytes">Space an ordinary user can still use.</param>
internal sealed record DriveSample(string Name, string? Label, long TotalBytes, long FreeBytes, long AvailableBytes);

internal interface IDriveReader
{
    IReadOnlyList<DriveSample> Read();
}

/// <summary>Reads fixed drives through <see cref="DriveInfo"/>, leaving out pseudo and container-internal mounts on Unix.</summary>
internal sealed class SystemDriveReader : IDriveReader
{
    private static readonly string[] IgnoredMountPrefixes =
        ["/proc", "/sys", "/dev", "/run", "/snap", "/etc", "/var/lib/docker", "/var/lib/containers", "/var/lib/kubelet", "/boot/efi"];

    public IReadOnlyList<DriveSample> Read()
    {
        var result = new List<DriveSample>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var drive in drives)
        {
            try
            {
                // DriveType is cheap; IsReady and the sizes can block on a sleeping or disconnected drive.
                if (drive.DriveType != DriveType.Fixed || IsIgnored(drive.Name) || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                result.Add(new DriveSample(drive.Name, VolumeLabel(drive), drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that went away while being read.
            }
        }

        return result;
    }

    private static bool IsIgnored(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        // Bind-mounted single files (Docker's /etc/hosts…) show up as drives; so do kernel and runtime mounts.
        return IgnoredMountPrefixes.Any(p => name.Equals(p, StringComparison.Ordinal) || name.StartsWith(p + "/", StringComparison.Ordinal)) ||
               File.Exists(name);
    }

    private static string? VolumeLabel(DriveInfo drive)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null; // on Linux the label would be the file system name, which is not a label
        }

        try
        {
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Publishes usage in percent, used bytes and free bytes of every fixed drive: <c>disk/{drive}/usage</c>,
/// <c>/used</c>, <c>/free</c>. Usage is computed like <c>df</c>: used / (used + available to users). Disk space
/// changes slowly, so drives are read every ten seconds.
/// </summary>
internal sealed class DiskUsageCollector(IDriveReader reader, TimeSpan? every = null) : ISystemCollector
{
    private const double StaleAfterSeconds = 60;

    private readonly Throttle _throttle = new(every ?? TimeSpan.FromSeconds(10));
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        if (!_throttle.Due(context.Time))
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var drive in reader.Read())
        {
            var slug = LocalIds.Unique(DriveSlug(drive.Name), used);
            seen.Add(slug);

            var usedBytes = Math.Max(0, drive.TotalBytes - drive.FreeBytes);
            var usable = usedBytes + drive.AvailableBytes;
            var device = drive.Label is null ? TrimRoot(drive.Name) : $"{TrimRoot(drive.Name)} ({drive.Label})";

            sensors.Define(Metrics.Number($"disk/{slug}/usage", "Usage", device, SensorCategory.Storage, SensorKinds.Load, Units.Percent, 0, 100, "harddisk") with { StaleAfterSeconds = StaleAfterSeconds });
            sensors.Define(Metrics.Number($"disk/{slug}/used", "Used", device, SensorCategory.Storage, SensorKinds.Data, Units.Bytes, 0, drive.TotalBytes, "harddisk") with { StaleAfterSeconds = StaleAfterSeconds });
            sensors.Define(Metrics.Number($"disk/{slug}/free", "Free", device, SensorCategory.Storage, SensorKinds.Data, Units.Bytes, 0, drive.TotalBytes, "harddisk-plus") with { StaleAfterSeconds = StaleAfterSeconds });
            sensors.Publish($"disk/{slug}/usage", usable > 0 ? Math.Clamp(100.0 * usedBytes / usable, 0, 100) : 0);
            sensors.Publish($"disk/{slug}/used", usedBytes);
            sensors.Publish($"disk/{slug}/free", drive.AvailableBytes);
        }

        foreach (var gone in _active.Except(seen).ToList())
        {
            sensors.RemovePrefix($"disk/{gone}/");
        }

        _active.Clear();
        _active.UnionWith(seen);
    }

    public void Dispose()
    {
    }

    /// <summary><c>C:\</c> gives <c>c</c>, <c>/</c> gives <c>root</c>, <c>/mnt/disk1</c> gives <c>mnt-disk1</c>.</summary>
    internal static string DriveSlug(string name) => name is "/" ? "root" : LocalIds.Slug(name, "drive");

    private static string TrimRoot(string name) => name.Length > 1 ? name.TrimEnd('\\', '/') : name;
}
