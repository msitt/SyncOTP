using SyncOTP.Core;

namespace SyncOTP.App.Output;

/// <summary>
/// Puts codes on the clipboard and takes them off again.
///
/// Two details matter. First, every call has to run on the UI thread, because the Win32 clipboard
/// is single-threaded-apartment only. Second, a 2FA code should not outlive its usefulness, so it
/// is marked to stay out of clipboard history and the cloud clipboard, and it is wiped after a
/// timeout unless the user has already copied something else.
/// </summary>
public sealed class ClipboardService
{
    // Undocumented but long-standing format names honoured by the Windows clipboard history and
    // cloud sync. Best-effort: a build that ignores them simply keeps the code in history.
    private const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    private const string ExcludeFromHistory = "CanIncludeInClipboardHistory";
    private const string ExcludeFromCloud = "CanUploadToCloudClipboard";

    private readonly Control _uiThread;
    private readonly System.Windows.Forms.Timer _clearTimer;
    private string? _pendingClear;

    public ClipboardService(Control uiThread)
    {
        _uiThread = uiThread;
        _clearTimer = new System.Windows.Forms.Timer();
        _clearTimer.Tick += (_, _) => ClearIfUnchanged();
    }

    /// <summary>Copies a code and schedules its removal. Returns false if the clipboard was locked.</summary>
    public bool SetCode(string code, int autoClearSeconds)
    {
        if (!Set(code, secret: true)) return false;

        _clearTimer.Stop();
        _pendingClear = null;

        if (autoClearSeconds > 0)
        {
            _pendingClear = code;
            _clearTimer.Interval = autoClearSeconds * 1000;
            _clearTimer.Start();
        }

        return true;
    }

    /// <summary>Copies ordinary text (the auth header, say) with no secrecy flags and no auto-clear.</summary>
    public bool SetText(string text) => Set(text, secret: false);

    private bool Set(string text, bool secret)
    {
        return Invoke(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Text, text);

            if (secret)
            {
                // A zero-byte payload is the documented way to say "no" to these consumers.
                var no = new MemoryStream([0]);
                data.SetData(ExcludeFromMonitoring, false, no);
                data.SetData(ExcludeFromHistory, false, new MemoryStream([0]));
                data.SetData(ExcludeFromCloud, false, new MemoryStream([0]));
            }

            // Another process can hold the clipboard open; a short retry is normal.
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch (Exception ex) when (attempt < 5)
                {
                    FileLog.Debug($"clipboard busy (attempt {attempt}): {ex.Message}");
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    FileLog.Warn($"could not write to the clipboard: {ex.Message}");
                    return false;
                }
            }

            return false;
        });
    }

    /// <summary>Wipes the clipboard, but only if it still holds the code we put there.</summary>
    public void ClearIfUnchanged()
    {
        _clearTimer.Stop();
        var expected = _pendingClear;
        _pendingClear = null;
        if (expected is null) return;

        Invoke(() =>
        {
            try
            {
                if (!Clipboard.ContainsText()) return false;
                if (!string.Equals(Clipboard.GetText(), expected, StringComparison.Ordinal)) return false;

                Clipboard.Clear();
                FileLog.Info("cleared the code from the clipboard");
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Debug($"clipboard clear skipped: {ex.Message}");
                return false;
            }
        });
    }

    private bool Invoke(Func<bool> action)
    {
        if (!_uiThread.IsHandleCreated) return false;

        return _uiThread.InvokeRequired
            ? (bool)_uiThread.Invoke(action)
            : action();
    }
}
