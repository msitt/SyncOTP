using System.Text.RegularExpressions;
using SyncOTP.App.Setup;
using Xunit;

namespace SyncOTP.App.Tests;

public class ExitSignalTests
{
    // Every test gets an event name of its own. ExitSignal.EventName is shared with any installed
    // copy of SyncOTP that is running, so signalling it from a test would shut that copy down (and
    // the test would fail, because the real app consumed the auto-reset signal first).
    private readonly string _eventName = $@"Local\SyncOTP.Tests.ExitRequest.{Guid.NewGuid():N}";

    [Fact]
    public void TheScriptSignalsTheEventTheAppListensOn()
    {
        // The tests below exercise a private name, so this pins the real one. If the two drift
        // apart, an update can no longer shut the app down cleanly and falls back to a kill,
        // which skips the clipboard wipe.
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "apply-payload.ps1"));
        var match = Regex.Match(script, @"^\$ExitEventName\s*=\s*'([^']+)'", RegexOptions.Multiline);

        Assert.True(match.Success, "apply-payload.ps1 no longer assigns $ExitEventName");
        Assert.Equal(ExitSignal.EventName, match.Groups[1].Value);
    }

    [Fact]
    public void SignallingTheNamedEventRunsTheCallback()
    {
        // This is the contract scripts/apply-payload.ps1 depends on to shut the app down cleanly
        // rather than killing it, which would skip the clipboard wipe.
        using var fired = new ManualResetEventSlim(false);
        using var signal = new ExitSignal(() => fired.Set(), _eventName);

        using var handle = EventWaitHandle.OpenExisting(_eventName);
        handle.Set();

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DisposingWithoutASignalDoesNotRunTheCallback()
    {
        var fired = false;

        using (var signal = new ExitSignal(() => fired = true, _eventName))
        {
            // Nothing signals it, so the waiter should come down via its own stop handle.
        }

        Thread.Sleep(100);
        Assert.False(fired);
    }

    [Fact]
    public void ASignalThatArrivedBeforeStartupIsIgnored()
    {
        // The regression test for an update that left nothing running. A named event outlives the
        // process that signalled it, so the applier's request would still be latched when the new
        // version started, and the new version would shut itself down again immediately.
        using (var stale = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName))
        {
            stale.Set();

            var fired = false;
            using var signal = new ExitSignal(() => fired = true, _eventName);

            Thread.Sleep(250);
            Assert.False(fired);
        }
    }

    [Fact]
    public void ASignalAfterStartupIsStillHonoured()
    {
        // The other half of the above: clearing the stale signal must not deafen us to a real one.
        using var stale = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
        stale.Set();

        using var fired = new ManualResetEventSlim(false);
        using var signal = new ExitSignal(() => fired.Set(), _eventName);

        Thread.Sleep(100);
        Assert.False(fired.IsSet);

        stale.Set();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        // ExitApp disposes it, and so does the ApplicationContext teardown right behind it.
        var signal = new ExitSignal(() => { }, _eventName);

        signal.Dispose();
        signal.Dispose();
    }
}
