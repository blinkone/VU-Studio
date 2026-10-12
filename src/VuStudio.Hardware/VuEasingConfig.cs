using VuStudio.Core.Profiles;

namespace VuStudio.Hardware;

/// <summary>A dial's on-device easing as returned by GET_EASING_CONFIG (four big-endian u32).</summary>
public readonly record struct VuEasingConfig(uint NeedleStep, uint NeedlePeriod, uint BacklightStep, uint BacklightPeriod)
{
    public Easing Needle => new(ToInt(NeedleStep), ToInt(NeedlePeriod));

    public Easing Backlight => new(ToInt(BacklightStep), ToInt(BacklightPeriod));

    private static int ToInt(uint value) => (int)Math.Min(value, int.MaxValue);
}
