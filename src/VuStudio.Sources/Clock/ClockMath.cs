namespace VuStudio.Sources.Clock;

/// <summary>
/// Calendar arithmetic behind the progress sensors. All functions take the <b>local wall-clock</b> time
/// and return a percentage in [0, 100). They treat every day as 24 hours, so a daylight-saving change
/// shifts the value by at most one hour's worth of progress.
/// </summary>
internal static class ClockMath
{
    /// <summary>Fraction of the day elapsed since local midnight.</summary>
    public static double DayProgress(DateTime local) => local.TimeOfDay.TotalDays * 100;

    /// <summary>Fraction of the week elapsed since the start of <paramref name="weekStart"/>.</summary>
    public static double WeekProgress(DateTime local, DayOfWeek weekStart)
    {
        var daysIntoWeek = ((int)local.DayOfWeek - (int)weekStart + 7) % 7;
        return (daysIntoWeek + local.TimeOfDay.TotalDays) / 7 * 100;
    }

    /// <summary>Fraction of the calendar month elapsed.</summary>
    public static double MonthProgress(DateTime local) =>
        ((local.Day - 1) + local.TimeOfDay.TotalDays) / DateTime.DaysInMonth(local.Year, local.Month) * 100;

    /// <summary>Fraction of the calendar year elapsed.</summary>
    public static double YearProgress(DateTime local) =>
        ((local.DayOfYear - 1) + local.TimeOfDay.TotalDays) / (DateTime.IsLeapYear(local.Year) ? 366 : 365) * 100;
}
