namespace VuStudio.Sources.Demo;

/// <summary>
/// A tiny seedable PRNG (SplitMix64). Unlike <see cref="Random"/> its sequence is fixed by definition, so
/// the demo signals and the tests that pin them stay identical across runtimes and platforms.
/// </summary>
internal sealed class SplitMix64
{
    private ulong _state;
    private double? _spareGaussian;

    public SplitMix64(int seed)
    {
        _state = unchecked((ulong)(long)seed);
    }

    public ulong NextUInt64()
    {
        var z = unchecked(_state += 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform in [<paramref name="min"/>, <paramref name="max"/>).</summary>
    public double NextRange(double min, double max) => min + ((max - min) * NextDouble());

    /// <summary>Standard normal (mean 0, deviation 1), by the polar Box-Muller method.</summary>
    public double NextGaussian()
    {
        if (_spareGaussian is { } spare)
        {
            _spareGaussian = null;
            return spare;
        }

        double u, v, s;
        do
        {
            u = (NextDouble() * 2) - 1;
            v = (NextDouble() * 2) - 1;
            s = (u * u) + (v * v);
        }
        while (s is >= 1 or 0);

        var factor = Math.Sqrt(-2 * Math.Log(s) / s);
        _spareGaussian = v * factor;
        return u * factor;
    }
}
