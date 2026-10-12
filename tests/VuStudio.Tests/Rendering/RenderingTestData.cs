using VuStudio.Core.Faces;
using VuStudio.Core.Mapping;

namespace VuStudio.Tests.Rendering;

/// <summary>Shared contexts and helpers for the rendering tests.</summary>
internal static class RenderingTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 12, 21, 45, 0, TimeSpan.Zero);

    public static IReadOnlyList<HistoryPoint> History(int count = 120, double center = 50, double amplitude = 20)
    {
        var points = new List<HistoryPoint>(count);
        for (var i = 0; i < count; i++)
        {
            points.Add(new HistoryPoint(Now.AddSeconds(-2 * (count - i)), center + (amplitude * Math.Sin(i / 7.0))));
        }

        return points;
    }

    public static FaceRenderContext Context(string title = "CPU", double? value = 63.4, string unit = "%", double min = 0, double max = 100, string? icon = "cpu-64-bit", int? decimals = null) => new()
    {
        Title = title,
        Value = value,
        Unit = unit,
        Icon = icon,
        Mapping = new ValueMapping(min, max),
        Decimals = decimals,
        SensorId = "test/sensor",
        History = (_, _) => History(),
        Now = Now,
    };

    /// <summary>The variety the themes must handle: short/long titles, small/large values and several units.</summary>
    public static IEnumerable<FaceRenderContext> Variety()
    {
        yield return Context("CPU", 63.4, "%");
        yield return Context("GPU HOT SPOT", 7, "°C", 0, 110, "thermometer");
        yield return Context("Download", 12345, "Mbit/s", 0, 20000, "download", decimals: 0);
        yield return Context("Pump", 1840, "RPM", 0, 3000, "fan", decimals: 0);
        yield return Context("A rather long sensor title that cannot fit", null, "", 0, 100, null);
    }

    public static int CountBlack(MonoBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        var count = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (!bitmap[x, y])
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>True when any pixel within <paramref name="radius"/> of (x, y) is black.</summary>
    public static bool BlackNear(MonoBitmap bitmap, double x, double y, int radius = 1)
    {
        var cx = (int)Math.Floor(x);
        var cy = (int)Math.Floor(y);
        return CountBlack(bitmap, cx - radius, cy - radius, cx + radius, cy + radius) > 0;
    }

    /// <summary>Horizontal extent of the black pixels (or null when blank).</summary>
    public static (int Left, int Right)? InkExtent(MonoBitmap bitmap)
    {
        int left = int.MaxValue, right = int.MinValue;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (!bitmap[x, y])
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }

        return left == int.MaxValue ? null : (left, right);
    }
}
