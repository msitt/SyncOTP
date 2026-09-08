using Microsoft.Toolkit.Uwp.Notifications;
using SyncOTP.Core;

namespace SyncOTP.App.Output;

/// <summary>
/// Tells the user a code arrived. Prefers a Windows toast, which can carry an action button, and
/// falls back to a tray balloon when toast registration is unavailable (an unpackaged app needs a
/// Start Menu shortcut, which the toolkit creates on first use but which can fail on locked-down
/// machines).
/// </summary>
public sealed class Notifier
{
    private const string ArgCopyAgain = "action=copyAgain";
    private const string ArgCode = "code";

    private readonly NotifyIcon _trayIcon;
    private bool _toastAvailable = true;

    /// <summary>Raised when the user clicks the toast or its "Copy again" button.</summary>
    public event Action<string>? CopyAgainRequested;

    public Notifier(NotifyIcon trayIcon)
    {
        _trayIcon = trayIcon;

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        }
        catch (Exception ex)
        {
            _toastAvailable = false;
            FileLog.Warn($"toast notifications unavailable, using tray balloons: {ex.Message}");
        }
    }

    /// <param name="snippet">
    /// A trimmed piece of the message body. The reported sender is usually an anonymous short
    /// code, so this is what tells the user which service the code belongs to. May be empty.
    /// </param>
    public void ShowCode(string code, string? from, string snippet, CodeConfidence confidence,
        bool useToast, bool sound)
    {
        var source = string.IsNullOrWhiteSpace(from) ? "" : $" from {from}";
        var qualifier = confidence == CodeConfidence.Low ? " (probable code)" : "";
        var status = $"Copied to the clipboard{source}.{qualifier}";

        if (useToast && _toastAvailable && TryToast(code, snippet, status, sound)) return;

        // A balloon has one body field, so the two lines are joined into it.
        var body = string.IsNullOrEmpty(snippet) ? status : $"{snippet}{Environment.NewLine}{status}";
        Balloon($"Code {code}", body, ToolTipIcon.Info);
    }

    public void ShowInfo(string title, string body) => Balloon(title, body, ToolTipIcon.Info);

    public void ShowWarning(string title, string body) => Balloon(title, body, ToolTipIcon.Warning);

    private bool TryToast(string code, string snippet, string status, bool sound)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ArgCode, code)
                .AddText($"Code {code}");

            // The generic toast template takes three text lines; the snippet earns the middle one
            // because it is the only part that names the service.
            if (!string.IsNullOrEmpty(snippet)) builder.AddText(snippet);

            builder
                .AddText(status)
                .AddButton(new ToastButton()
                    .SetContent("Copy again")
                    .AddArgument(ArgCopyAgain)
                    .AddArgument(ArgCode, code));

            if (!sound) builder.AddAudio(new ToastAudio { Silent = true });

            builder.Show(toast =>
            {
                // A code is only useful for a minute or two; do not let it pile up in the
                // notification centre.
                toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(5);
            });

            return true;
        }
        catch (Exception ex)
        {
            _toastAvailable = false;
            FileLog.Warn($"toast failed, falling back to tray balloons: {ex.Message}");
            return false;
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue(ArgCode, out var code) || string.IsNullOrEmpty(code)) return;

            CopyAgainRequested?.Invoke(code);
        }
        catch (Exception ex)
        {
            FileLog.Debug($"toast activation ignored: {ex.Message}");
        }
    }

    private void Balloon(string title, string body, ToolTipIcon icon)
    {
        try
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = body;
            _trayIcon.BalloonTipIcon = icon;
            _trayIcon.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            FileLog.Debug($"balloon tip failed: {ex.Message}");
        }
    }

    /// <summary>Removes the toolkit's COM registration artefacts on exit.</summary>
    public static void Cleanup()
    {
        try { ToastNotificationManagerCompat.History.Clear(); }
        catch { /* nothing registered */ }
    }
}
