using SyncOTP.App.Updates;
using Xunit;

namespace SyncOTP.App.Tests;

public class InstallLocationTests
{
    [Fact]
    public void DetectFindsTheDirectoryTheTestHostIsRunningFrom()
    {
        var location = InstallLocation.Detect();

        Assert.False(string.IsNullOrWhiteSpace(location.ExePath));
        Assert.True(Directory.Exists(location.Directory));
    }

    [Fact]
    public void RunningFromBuildOutputIsBlocked()
    {
        // The test host runs out of bin\Debug\..., which is exactly the case the guard exists for:
        // a developer's build output is perfectly writable and still the wrong thing to overwrite,
        // because the next dotnet build would silently undo the update.
        var location = InstallLocation.Detect();

        Assert.False(location.CanSelfUpdate);
        Assert.NotNull(location.BlockedReason);
        Assert.Contains("build output", location.BlockedReason);
    }

    [Fact]
    public void ABlockedInstallStillReportsWhereItIs()
    {
        // Blocked means "do not swap the files", not "do not check". The tray still offers the
        // release page, so the location has to survive the refusal intact.
        var location = InstallLocation.Detect();

        Assert.False(location.CanSelfUpdate);
        Assert.True(Path.IsPathFullyQualified(location.ExePath));
    }
}
