using System.Globalization;
using SyncOTP.Core;
using Xunit;

namespace SyncOTP.Core.Tests;

public class UpdatesConfigTests
{
    [Fact]
    public void UpdatesAreCheckedDailyByDefault()
    {
        var updates = new UpdatesConfig();

        Assert.True(updates.CheckAutomatically);
        Assert.False(updates.DownloadAutomatically);
        Assert.False(updates.AllowPreRelease);
        Assert.Equal(TimeSpan.FromHours(24), updates.Interval);
    }

    [Fact]
    public void TheIntervalIsClampedToAtLeastAnHour()
    {
        // Nothing should let a hand-edited config hammer the release host on every timer tick.
        Assert.Equal(TimeSpan.FromHours(1), new UpdatesConfig { CheckIntervalHours = 0 }.Interval);
        Assert.Equal(TimeSpan.FromHours(1), new UpdatesConfig { CheckIntervalHours = -5 }.Interval);
    }

    [Fact]
    public void AnEmptyLastCheckMeansNeverChecked()
    {
        Assert.Null(new UpdatesConfig().LastCheck);
    }

    [Fact]
    public void AGarbageLastCheckMeansNeverChecked()
    {
        // A typo in a hand-edited file costs one extra check, it does not throw during Load.
        Assert.Null(new UpdatesConfig { LastCheckUtc = "yesterday" }.LastCheck);
    }

    [Fact]
    public void ARoundTripTimestampParsesBackToTheSameInstant()
    {
        var moment = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var updates = new UpdatesConfig
        {
            LastCheckUtc = moment.ToString("o", CultureInfo.InvariantCulture),
        };

        Assert.Equal(moment, updates.LastCheck);
    }

    [Fact]
    public void AFreshConfigHasAnUpdatesBlock()
    {
        Assert.NotNull(new Config().Updates);
    }
}
