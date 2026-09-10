using SyncOTP.App.Setup;
using Xunit;

namespace SyncOTP.App.Tests;

public class ExitSignalTests
{
    [Fact]
    public void SignallingTheNamedEventRunsTheCallback()
    {
        // This is the contract scripts/apply-payload.ps1 depends on to shut the app down cleanly
        // rather than killing it, which would skip the clipboard wipe.
        using var fired = new ManualResetEventSlim(false);
        using var signal = new ExitSignal(() => fired.Set());

        using var handle = EventWaitHandle.OpenExisting(ExitSignal.EventName);
        handle.Set();

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DisposingWithoutASignalDoesNotRunTheCallback()
    {
        var fired = false;

        using (var signal = new ExitSignal(() => fired = true))
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
        using (var stale = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignal.EventName))
        {
            stale.Set();

            var fired = false;
            using var signal = new ExitSignal(() => fired = true);

            Thread.Sleep(250);
            Assert.False(fired);
        }
    }

    [Fact]
    public void ASignalAfterStartupIsStillHonoured()
    {
        // The other half of the above: clearing the stale signal must not deafen us to a real one.
        using var stale = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignal.EventName);
        stale.Set();

        using var fired = new ManualResetEventSlim(false);
        using var signal = new ExitSignal(() => fired.Set());

        Thread.Sleep(100);
        Assert.False(fired.IsSet);

        stale.Set();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        // ExitApp disposes it, and so does the ApplicationContext teardown right behind it.
        var signal = new ExitSignal(() => { });

        signal.Dispose();
        signal.Dispose();
    }
}
