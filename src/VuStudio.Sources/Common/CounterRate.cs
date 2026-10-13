namespace VuStudio.Sources.Common;

/// <summary>
/// Turns monotonically increasing counters (bytes transferred, sectors read…) into per-second rates.
/// Keeps one previous sample per key. Not thread-safe.
/// </summary>
internal sealed class CounterRate
{
    private readonly Dictionary<string, (double Counter, TimeSpan Time)> _last = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a sample and returns the rate since the previous one, or <c>null</c> for the first sample, when
    /// no time has passed, or when the counter went backwards (reset, wrap or interface re-created); in the
    /// latter case the new value becomes the baseline.
    /// </summary>
    public double? Update(string key, double counter, TimeSpan time)
    {
        if (!_last.TryGetValue(key, out var previous))
        {
            _last[key] = (counter, time);
            return null;
        }

        var seconds = (time - previous.Time).TotalSeconds;
        if (seconds <= 0)
        {
            return null;
        }

        _last[key] = (counter, time);
        var delta = counter - previous.Counter;
        return delta < 0 ? null : delta / seconds;
    }

    public void Forget(string key) => _last.Remove(key);

    public void ForgetPrefix(string prefix)
    {
        foreach (var key in _last.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _last.Remove(key);
        }
    }
}
