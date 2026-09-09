using SyncOTP.Core;
using Xunit;

namespace SyncOTP.Core.Tests;

public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);

    [Fact]
    public void AnAppThatHasNeverCheckedIsDue()
    {
        Assert.True(UpdateSchedule.IsDue(null, Now, Daily));
    }

    [Fact]
    public void ACheckInsideTheIntervalIsNotDue()
    {
        Assert.False(UpdateSchedule.IsDue(Now.AddHours(-3), Now, Daily));
    }

    [Fact]
    public void ACheckExactlyAtTheIntervalIsDue()
    {
        Assert.True(UpdateSchedule.IsDue(Now.AddHours(-24), Now, Daily));
    }

    [Fact]
    public void ACheckTimestampInTheFutureDoesNotWedgeTheSchedule()
    {
        // config.json roams, so the stamp can come from another machine or from before a clock fix.
        // Waiting for the clock to catch up could disable updates for months.
        Assert.True(UpdateSchedule.IsDue(Now.AddDays(30), Now, Daily));
    }

    [Fact]
    public void TheFirstEvaluationWaitsTheStartupDelay()
    {
        // Even when a check is overdue, the first one stays off the startup path.
        var delay = UpdateSchedule.NextDelay(null, Now, Daily, firstEvaluation: true);

        Assert.Equal(UpdateSchedule.StartupDelay, delay);
    }

    [Fact]
    public void AStillDueCheckComesBackAtTheMinimumStep()
    {
        // A failed check does not record a timestamp, so it stays due and retries shortly.
        var delay = UpdateSchedule.NextDelay(null, Now, Daily, firstEvaluation: false);

        Assert.Equal(UpdateSchedule.MinTimerStep, delay);
    }

    [Fact]
    public void ARecentCheckSchedulesTheRemainingTime()
    {
        var delay = UpdateSchedule.NextDelay(Now.AddHours(-22), Now, Daily, firstEvaluation: false);

        Assert.Equal(TimeSpan.FromHours(2), delay);
    }

    [Fact]
    public void TheDelayNeverExceedsTheTimerCeiling()
    {
        // A WinForms timer does not fire while the machine sleeps, so it re-checks the wall clock
        // several times a day rather than sleeping for the whole interval.
        var delay = UpdateSchedule.NextDelay(Now, Now, Daily, firstEvaluation: false);

        Assert.Equal(UpdateSchedule.MaxTimerStep, delay);
    }
}
