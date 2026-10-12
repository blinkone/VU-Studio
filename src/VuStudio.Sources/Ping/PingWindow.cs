namespace VuStudio.Sources.Pings;

/// <summary>The results of the most recent pings of one target, from which the packet loss is computed.</summary>
internal sealed class PingWindow
{
    private readonly bool[] _results;
    private int _next;
    private int _count;
    private int _failures;

    public PingWindow(int size)
    {
        _results = new bool[Math.Max(1, size)];
    }

    /// <summary>How many results are held (at most the window size).</summary>
    public int Count => _count;

    /// <summary>Share of failed pings among those held, 0-100; 0 while empty.</summary>
    public double LossPercent => _count == 0 ? 0 : 100.0 * _failures / _count;

    public void Add(bool success)
    {
        if (_count == _results.Length && !_results[_next])
        {
            _failures--; // the oldest result, a failure, is about to be overwritten
        }

        _results[_next] = success;
        if (!success)
        {
            _failures++;
        }

        _next = (_next + 1) % _results.Length;
        _count = Math.Min(_count + 1, _results.Length);
    }

    public void Reset()
    {
        _next = 0;
        _count = 0;
        _failures = 0;
    }
}
