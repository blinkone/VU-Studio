namespace VuStudio.Session;

/// <summary>Exponential backoff: <c>initial</c>, then doubling up to <c>max</c>. Not thread-safe.</summary>
public sealed class Backoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private TimeSpan _next;

    public Backoff(TimeSpan initial, TimeSpan max)
    {
        if (initial <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initial));
        }

        _initial = initial;
        _max = max < initial ? initial : max;
        _next = initial;
    }

    /// <summary>Returns the delay to wait now and grows the next one.</summary>
    public TimeSpan Next()
    {
        var current = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, _max.Ticks));
        return current;
    }

    public void Reset() => _next = _initial;
}
