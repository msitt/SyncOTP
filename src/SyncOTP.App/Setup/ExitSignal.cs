using SyncOTP.Core;

namespace SyncOTP.App.Setup;

/// <summary>
/// Lets another process ask SyncOTP to shut down properly instead of killing it. A forced kill
/// skips the clipboard wipe and the toast cleanup, which is exactly the wrong way to end an app
/// whose whole job is not leaving codes lying around.
///
/// Used by scripts/apply-payload.ps1 before it replaces the install directory.
/// </summary>
public sealed class ExitSignal : IDisposable
{
    public const string EventName = @"Local\SyncOTP.ExitRequest";

    private readonly EventWaitHandle? _handle;
    private readonly ManualResetEventSlim _stopping = new(false);
    private readonly Thread? _waiter;
    private bool _disposed;

    /// <param name="onRequested">
    /// Raised on a background thread. The caller marshals onto the UI thread, the same way the
    /// message sources do.
    /// </param>
    public ExitSignal(Action onRequested)
    {
        try
        {
            _handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

            // Discard anything already latched on the event. A named event outlives the process
            // that signalled it, and the initial state is ignored when the handle already exists,
            // so a signal nobody consumed would otherwise be delivered to this instance the moment
            // it starts. That is exactly what happens after an update: the applier signals, the old
            // app exits, and the new one would shut down again immediately, leaving the user with
            // nothing running. A request that arrived before we existed was not meant for us.
            _handle.Reset();
        }
        catch (Exception ex)
        {
            // Locked-down machines can refuse a named handle. The app works fine without one, the
            // installer just falls back to stopping the process.
            FileLog.Warn($"exit-request handle unavailable, external shutdown requests will not work: {ex.Message}");
            return;
        }

        _waiter = new Thread(() => Wait(onRequested))
        {
            IsBackground = true,
            Name = "SyncOTP exit signal",
        };
        _waiter.Start();
    }

    private void Wait(Action onRequested)
    {
        try
        {
            var signalled = WaitHandle.WaitAny([_handle!, _stopping.WaitHandle]);

            // Index 1 is our own shutdown, which means the app is already on its way out.
            if (signalled != 0) return;

            FileLog.Info("exit requested by another process");
            onRequested();
        }
        catch (AbandonedMutexException)
        {
            // Nothing to recover: the requester died before we noticed.
        }
        catch (Exception ex)
        {
            FileLog.Debug($"exit-signal waiter stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // ExitApp disposes this, and so does the ApplicationContext teardown behind it.
        if (_disposed) return;
        _disposed = true;

        _stopping.Set();
        _handle?.Dispose();
        _stopping.Dispose();
    }
}
