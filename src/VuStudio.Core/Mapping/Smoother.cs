namespace VuStudio.Core.Mapping;

/// <summary>
/// Asymmetric exponential smoothing with optional peak hold. With a short attack and a long
/// release it reproduces PPM-style ballistics; equal times give a classic VU-style integration.
/// </summary>
public sealed class Smoother
{
    private double? _current;
    private double _held = double.NegativeInfinity;
    private double _holdRemainingMs;

    /// <summary>Time constant (ms) used while the target is above the current value. 0 = instant.</summary>
    public double AttackMs { get; set; }

    /// <summary>Time constant (ms) used while the target is below the current value. 0 = instant.</summary>
    public double ReleaseMs { get; set; }

    /// <summary>How long (ms) a new peak is held before the release starts. 0 = no hold.</summary>
    public double HoldMs { get; set; }

    public double? Current => _current;

    public void Reset() => Reset(null);

    public void Reset(double? value)
    {
        _current = value;
        _held = double.NegativeInfinity;
        _holdRemainingMs = 0;
    }

    /// <summary>Advances the filter by <paramref name="elapsedMs"/> towards <paramref name="target"/>.</summary>
    public double Update(double target, double elapsedMs)
    {
        if (double.IsNaN(target))
        {
            return _current ?? 0;
        }

        elapsedMs = Math.Max(0, elapsedMs);

        if (HoldMs > 0)
        {
            if (target >= _held)
            {
                _held = target;
                _holdRemainingMs = HoldMs;
            }
            else
            {
                _holdRemainingMs -= elapsedMs;
                if (_holdRemainingMs > 0)
                {
                    target = _held;
                }
                else
                {
                    _held = target;
                }
            }
        }

        if (_current is not { } current)
        {
            _current = target;
            return target;
        }

        var tau = target > current ? AttackMs : ReleaseMs;
        if (tau <= 0)
        {
            _current = target;
        }
        else
        {
            var alpha = 1 - Math.Exp(-elapsedMs / tau);
            _current = current + ((target - current) * alpha);
        }

        return _current.Value;
    }
}
