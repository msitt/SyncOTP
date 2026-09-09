namespace SyncOTP.Core;

/// <summary>
/// When the next update check is due. Takes the current time as a parameter rather than reading
/// the clock, the same way <see cref="MessageDeduper"/> does, so the awkward cases are testable.
/// </summary>
public static class UpdateSchedule
{
    /// <summary>Long enough after launch that a check never competes with the first ntfy connect.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A WinForms timer does not fire while the machine is asleep, so the timer re-evaluates
    /// against the wall clock several times a day instead of sleeping for the whole interval.
    /// Without this, "every 24 hours" quietly becomes "every 24 hours of uptime" on a laptop.
    /// </summary>
    public static readonly TimeSpan MaxTimerStep = TimeSpan.FromHours(6);

    public static readonly TimeSpan MinTimerStep = TimeSpan.FromMinutes(1);

    public static bool IsDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc, TimeSpan interval)
    {
        if (lastCheckUtc is not { } last) return true;

        // config.json roams, so the timestamp can arrive from another machine or from before the
        // clock was corrected. Treat the future as due rather than waiting for the clock to catch up.
        if (last > nowUtc) return true;

        return nowUtc - last >= interval;
    }

    /// <summary>How long the tray timer should wait before it evaluates again.</summary>
    public static TimeSpan NextDelay(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc,
                                     TimeSpan interval, bool firstEvaluation)
    {
        if (firstEvaluation) return StartupDelay;

        // Still due means the last attempt failed and did not record a timestamp, so come back soon.
        if (IsDue(lastCheckUtc, nowUtc, interval)) return MinTimerStep;

        var remaining = lastCheckUtc!.Value + interval - nowUtc;

        if (remaining > MaxTimerStep) return MaxTimerStep;
        if (remaining < MinTimerStep) return MinTimerStep;
        return remaining;
    }
}
