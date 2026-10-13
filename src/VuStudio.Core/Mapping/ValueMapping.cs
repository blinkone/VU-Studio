namespace VuStudio.Core.Mapping;

public enum ScaleType
{
    Linear,

    /// <summary>Logarithmic. Works with a zero minimum by spanning <see cref="ValueMapping.Decades"/> decades.</summary>
    Logarithmic,

    /// <summary>Square-root: expands the low end, softer than logarithmic.</summary>
    SquareRoot,

    /// <summary>Piecewise linear through <see cref="ValueMapping.Points"/>.</summary>
    Custom,
}

/// <summary>A point of a custom scale: <see cref="Value"/> sits at <see cref="Position"/> (0..1) on the dial.</summary>
public sealed record ScalePoint(double Value, double Position);

/// <summary>
/// Maps values (in display units) to a dial fraction 0..1 and back. The same mapping drives the
/// needle and the printed scale, so labels always line up with the needle.
/// </summary>
public sealed class ValueMapping
{
    public ValueMapping(double min, double max, ScaleType scale = ScaleType.Linear, IReadOnlyList<ScalePoint>? points = null, bool invert = false, double decades = 3)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            throw new ArgumentException("Range must be finite.");
        }

        if (max == min)
        {
            max = min + 1;
        }

        Min = Math.Min(min, max);
        Max = Math.Max(min, max);
        Invert = invert ^ (max < min);
        Decades = Math.Clamp(decades, 1, 9);

        if (scale == ScaleType.Custom)
        {
            var ordered = (points ?? [])
                .Where(p => double.IsFinite(p.Value) && double.IsFinite(p.Position))
                .OrderBy(p => p.Value)
                .ToList();
            if (ordered.Count < 2)
            {
                scale = ScaleType.Linear;
            }
            else
            {
                Points = ordered;
                Min = ordered[0].Value;
                Max = ordered[^1].Value;
            }
        }

        Scale = scale;
    }

    public double Min { get; }

    public double Max { get; }

    public ScaleType Scale { get; }

    public bool Invert { get; }

    /// <summary>Number of decades shown by a logarithmic scale (the low end is Max / 10^Decades).</summary>
    public double Decades { get; }

    public IReadOnlyList<ScalePoint>? Points { get; }

    public double Range => Max - Min;

    public static ValueMapping Percent { get; } = new(0, 100);

    /// <summary>Converts a value to a dial fraction, clamped to 0..1.</summary>
    public double ToFraction(double value)
    {
        if (double.IsNaN(value))
        {
            return Invert ? 1 : 0;
        }

        var f = Scale switch
        {
            ScaleType.Logarithmic => LogFraction(value),
            ScaleType.SquareRoot => Math.Sqrt(Math.Max(0, value - Min) / Range),
            ScaleType.Custom => CustomFraction(value),
            _ => (value - Min) / Range,
        };
        f = Math.Clamp(f, 0, 1);
        return Invert ? 1 - f : f;
    }

    /// <summary>Converts a dial fraction (0..1) back to a value.</summary>
    public double FromFraction(double fraction)
    {
        var f = Math.Clamp(fraction, 0, 1);
        if (Invert)
        {
            f = 1 - f;
        }

        return Scale switch
        {
            ScaleType.Logarithmic => LogValue(f),
            ScaleType.SquareRoot => Min + (f * f * Range),
            ScaleType.Custom => CustomValue(f),
            _ => Min + (f * Range),
        };
    }

    /// <summary>
    /// Suggests "nice" values for major scale labels: round numbers for linear scales, decades (1-2-5)
    /// for logarithmic ones, and the explicit points for custom scales.
    /// </summary>
    public IReadOnlyList<double> SuggestLabelValues(int approximateCount = 5)
    {
        approximateCount = Math.Clamp(approximateCount, 2, 12);
        switch (Scale)
        {
            case ScaleType.Custom:
                return Points!.Select(p => p.Value).ToList();
            case ScaleType.Logarithmic:
            {
                var list = new List<double> { Min };
                var epsilon = LogEpsilon;
                var start = Math.Pow(10, Math.Ceiling(Math.Log10(Math.Max(epsilon, 1e-12))));
                for (var v = start; v <= Max * 1.0000001; v *= 10)
                {
                    if (Min + v > Min && Min + v <= Max)
                    {
                        list.Add(Min + v);
                    }
                }

                if (list[^1] < Max)
                {
                    list.Add(Max);
                }

                return Thin(list.Distinct().ToList(), approximateCount + 1);
            }

            default:
            {
                var step = NiceStep(Range / (approximateCount - 1));
                var list = new List<double>();
                var first = Math.Ceiling(Min / step) * step;
                for (var v = first; v <= Max + (step * 1e-9); v += step)
                {
                    list.Add(Math.Round(v / step) * step);
                }

                if (list.Count == 0 || Math.Abs(list[0] - Min) > step * 0.25)
                {
                    list.Insert(0, Min);
                }

                if (Math.Abs(list[^1] - Max) > step * 0.25)
                {
                    list.Add(Max);
                }

                return list;
            }
        }
    }

    /// <summary>Rounds a raw step to 1, 2, 2.5 or 5 times a power of ten.</summary>
    public static double NiceStep(double rough)
    {
        if (rough <= 0 || !double.IsFinite(rough))
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(rough));
        var magnitude = Math.Pow(10, exponent);
        var fraction = rough / magnitude;
        var nice = fraction switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10,
        };
        return nice * magnitude;
    }

    private double LogEpsilon => Range / Math.Pow(10, Decades);

    private double LogFraction(double value)
    {
        var v = Math.Max(0, value - Min);
        var eps = LogEpsilon;
        return Math.Log10(1 + (v / eps)) / Math.Log10(1 + (Range / eps));
    }

    private double LogValue(double f)
    {
        var eps = LogEpsilon;
        return Min + (eps * (Math.Pow(10, f * Math.Log10(1 + (Range / eps))) - 1));
    }

    private double CustomFraction(double value)
    {
        var pts = Points!;
        if (value <= pts[0].Value)
        {
            return pts[0].Position;
        }

        for (var i = 1; i < pts.Count; i++)
        {
            if (value <= pts[i].Value)
            {
                var a = pts[i - 1];
                var b = pts[i];
                var t = (value - a.Value) / (b.Value - a.Value);
                return a.Position + (t * (b.Position - a.Position));
            }
        }

        return pts[^1].Position;
    }

    private double CustomValue(double f)
    {
        var pts = Points!;
        for (var i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var lo = Math.Min(a.Position, b.Position);
            var hi = Math.Max(a.Position, b.Position);
            if (f >= lo && f <= hi && hi > lo)
            {
                var t = (f - a.Position) / (b.Position - a.Position);
                return a.Value + (t * (b.Value - a.Value));
            }
        }

        return f <= pts[0].Position ? pts[0].Value : pts[^1].Value;
    }

    private static List<double> Thin(List<double> values, int max)
    {
        if (values.Count <= max)
        {
            return values;
        }

        var result = new List<double>();
        var stride = (double)(values.Count - 1) / (max - 1);
        for (var i = 0; i < max; i++)
        {
            result.Add(values[(int)Math.Round(i * stride)]);
        }

        return result.Distinct().ToList();
    }
}
