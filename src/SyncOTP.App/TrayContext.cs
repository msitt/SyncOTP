using System.Diagnostics;
using SyncOTP.App.Output;
using SyncOTP.App.Setup;
using SyncOTP.App.Sources;
using SyncOTP.Core;

namespace SyncOTP.App;

/// <summary>
/// The whole application: a tray icon, a set of message sources, and the pipeline that turns an
/// incoming SMS into a code on the clipboard.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Control _uiThread;
    private readonly ClipboardService _clipboard;
    private readonly Notifier _notifier;
    private readonly CodeHistory _history = new();
    private readonly System.Windows.Forms.Timer _iconResetTimer;

    private Config _config;
    private CodeExtractor _extractor;
    private MessageDeduper _deduper = new();
    private NtfySource? _source;
    private CancellationTokenSource _sourceCts = new();
    private bool _paused;

    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _historyItem = null!;
    private ToolStripMenuItem _pauseItem = null!;
    private ToolStripMenuItem _startupItem = null!;

    public TrayContext(Config config, bool configCreated)
    {
        _config = config;
        _extractor = new CodeExtractor(config.Extractor);

        // A hidden window gives us a handle to marshal clipboard and UI work onto.
        _uiThread = new Control();
        _uiThread.CreateControl();

        _clipboard = new ClipboardService(_uiThread);

        _trayIcon = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Text = "SyncOTP",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _trayIcon.MouseClick += OnTrayClick;

        _notifier = new Notifier(_trayIcon);
        _notifier.CopyAgainRequested += code => _uiThread.BeginInvoke(() => CopyExisting(code));

        _iconResetTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _iconResetTimer.Tick += (_, _) =>
        {
            _iconResetTimer.Stop();
            UpdateIcon();
        };

        BuildMenu();
        StartSource();

        if (configCreated)
        {
            FileLog.Info($"first run: created {Paths.ConfigFile}");
            _notifier.ShowInfo("SyncOTP needs setting up",
                "Fill in your ntfy password in config.json, then choose Reload config.");
            OpenConfig();
        }
    }

    // ---- menu ---------------------------------------------------------------------------------

    private void BuildMenu()
    {
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _historyItem = new ToolStripMenuItem("Recent codes");
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = StartupRegistration.IsEnabled(),
        };

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_historyItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Send test message", null, async (_, _) => await SendTestAsync()));
        _menu.Items.Add(new ToolStripMenuItem("Copy Shortcut auth header", null, (_, _) => CopyAuthHeader()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Open config", null, (_, _) => OpenConfig()));
        _menu.Items.Add(new ToolStripMenuItem("Reload config", null, (_, _) => ReloadConfig()));
        _menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()));
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _menu.Opening += (_, _) => RefreshHistoryMenu();

        RefreshHistoryMenu();
    }

    private void RefreshHistoryMenu()
    {
        _historyItem.DropDownItems.Clear();
        var recent = _history.Recent();

        if (recent.Count == 0)
        {
            _historyItem.DropDownItems.Add(new ToolStripMenuItem("No codes yet") { Enabled = false });
            return;
        }

        foreach (var entry in recent)
        {
            var code = entry.Code;
            _historyItem.DropDownItems.Add(new ToolStripMenuItem(entry.MenuLabel, null, (_, _) => CopyExisting(code)));
        }
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var latest = _history.Latest();
        if (latest is null)
        {
            _notifier.ShowInfo("SyncOTP", "No codes received yet.");
            return;
        }

        CopyExisting(latest.Code);
    }

    // ---- pipeline -----------------------------------------------------------------------------

    private void StartSource()
    {
        _sourceCts = new CancellationTokenSource();
        _source = new NtfySource(_config);
        _source.MessageReceived += OnMessage;
        _source.StateChanged += OnSourceState;
        _source.Progressed += id => _config.SaveLastId(id);

        var source = _source;
        var token = _sourceCts.Token;
        _ = Task.Run(() => source.StartAsync(token), token);
    }

    private void OnSourceState(IMessageSource source, SourceState state)
    {
        _uiThread.BeginInvoke(() =>
        {
            var text = state.Status switch
            {
                SourceStatus.Connected => "ntfy: connected",
                SourceStatus.Connecting => "ntfy: connecting…",
                SourceStatus.Reconnecting => $"ntfy: reconnecting ({state.Detail})",
                SourceStatus.AuthFailed => "ntfy: auth failed, check config.json",
                SourceStatus.NotConfigured => "ntfy: not configured",
                _ => "ntfy: stopped",
            };

            _statusItem.Text = text;
            UpdateIcon();

            if (state.Status == SourceStatus.AuthFailed)
                _notifier.ShowWarning("SyncOTP cannot sign in to ntfy", state.Detail);
            else if (state.Status == SourceStatus.NotConfigured)
                _notifier.ShowWarning("SyncOTP is not configured", state.Detail);
        });
    }

    private void OnMessage(IncomingMessage message)
    {
        _uiThread.BeginInvoke(() => ProcessMessage(message));
    }

    private void ProcessMessage(IncomingMessage message)
    {
        var snippet = message.Snippet(_config.MessageSnippetChars);
        var origin = describeOrigin();

        if (_config.VerboseLogging)
            FileLog.Debug($"[{message.SourceName}] from={message.From ?? "?"} text={message.Text}");

        if (!_deduper.IsNew(message))
        {
            FileLog.Debug($"duplicate message {message.Id} ignored");
            return;
        }

        if (_paused)
        {
            FileLog.Info($"message {origin} ignored: SyncOTP is paused");
            return;
        }

        var extracted = _extractor.Extract(message.Text);
        if (extracted is null)
        {
            FileLog.Info($"no code found in a {message.Text.Length}-character message {origin}");
            return;
        }

        // On a reconnect ntfy replays whatever it cached. Those codes have almost certainly expired,
        // and silently overwriting the clipboard with one would be worse than doing nothing, so
        // record them but leave the clipboard alone.
        var stale = message.Age > TimeSpan.FromSeconds(_config.Clipboard.StaleAfterSeconds);

        _history.Add(new CodeEntry(extracted.Code, message.From, DateTimeOffset.Now));
        RefreshHistoryMenu();

        if (stale)
        {
            FileLog.Info($"code {extracted.Code} {origin} is {message.Age.TotalMinutes:0} minutes old; " +
                         "added to Recent codes but not copied");
            return;
        }

        var copied = _clipboard.SetCode(extracted.Code, _config.Clipboard.AutoClearSeconds);
        FileLog.Info($"code {extracted.Code} {origin} " +
                     $"({extracted.Confidence}, {extracted.Reason}){(copied ? " copied" : " NOT copied: clipboard locked")}");

        _notifier.ShowCode(extracted.Code, message.From, snippet, extracted.Confidence,
            _config.Notifications.Toast, _config.Notifications.Sound);

        _trayIcon.Icon = TrayIcons.Active;
        _trayIcon.Text = Truncate($"SyncOTP, last code {extracted.Code}");
        _iconResetTimer.Stop();
        _iconResetTimer.Start();

        // The reported sender is nearly always an anonymous short code, so the body text is what
        // actually identifies the service. Both go in the line where they are available.
        string describeOrigin()
        {
            var sender = string.IsNullOrWhiteSpace(message.From) ? "unknown" : message.From;
            return snippet.Length == 0 ? $"from {sender}" : $"from {sender}: \"{snippet}\"";
        }
    }

    private void CopyExisting(string code)
    {
        if (_clipboard.SetCode(code, _config.Clipboard.AutoClearSeconds))
            _notifier.ShowInfo($"Code {code}", "Copied to the clipboard.");
        else
            _notifier.ShowWarning("Could not copy", "The clipboard is in use by another program.");
    }

    // ---- menu actions -------------------------------------------------------------------------

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Checked = _paused;
        _pauseItem.Text = _paused ? "Paused" : "Pause";
        UpdateIcon();
        FileLog.Info(_paused ? "paused" : "resumed");
    }

    private void ToggleStartup()
    {
        var enable = !_startupItem.Checked;
        StartupRegistration.SetEnabled(enable);
        _startupItem.Checked = StartupRegistration.IsEnabled();

        if (_startupItem.Checked != enable)
            _notifier.ShowWarning("Could not change startup", "See the log for details.");
    }

    private async Task SendTestAsync()
    {
        if (_source is null) return;

        var code = Random.Shared.Next(100000, 999999).ToString();
        try
        {
            await _source.PublishAsync($"Your SyncOTP test code is {code}. It expires in 10 minutes.", "SyncOTP test");
            FileLog.Info($"test message published with code {code}");
        }
        catch (Exception ex)
        {
            FileLog.Error("test message failed", ex);
            _notifier.ShowWarning("Test message failed", ex.Message);
        }
    }

    private void CopyAuthHeader()
    {
        if (!_config.Ntfy.HasCredentials)
        {
            _notifier.ShowInfo("No auth header needed",
                $"The topic is unauthenticated. The Shortcut just POSTs to {_config.Ntfy.TopicUrl}.");
            return;
        }

        var header = _config.Ntfy.BuildBasicAuthHeader();
        _clipboard.SetText(header);

        // Written to the log too, so it can be retrieved when setting the Shortcut up on the phone.
        FileLog.Info($"Shortcut auth header: {header}");
        _notifier.ShowInfo("Auth header copied",
            $"Paste this as the Authorization header in the Shortcut. POST to {_config.Ntfy.TopicUrl}");
    }

    private void OpenConfig()
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile)) _config.Save();
            Process.Start(new ProcessStartInfo(Paths.ConfigFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Error("could not open config.json", ex);
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(FileLog.CurrentFile)) FileLog.Info("log opened");
            Process.Start(new ProcessStartInfo(FileLog.CurrentFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Error("could not open the log", ex);
        }
    }

    private void ReloadConfig()
    {
        FileLog.Info("reloading config");

        StopSource();

        _config = Config.Load(out _);
        _extractor = new CodeExtractor(_config.Extractor);
        _deduper = new MessageDeduper();
        FileLog.MinLevel = _config.VerboseLogging ? Core.LogLevel.Debug : Core.LogLevel.Info;

        StartSource();
        _notifier.ShowInfo("SyncOTP", "Configuration reloaded.");
    }

    private void StopSource()
    {
        try
        {
            _sourceCts.Cancel();
            _sourceCts.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Debug($"stopping the source: {ex.Message}");
        }

        _source = null;
    }

    private void UpdateIcon()
    {
        var status = _source?.State.Status ?? SourceStatus.Stopped;

        _trayIcon.Icon = _paused
            ? TrayIcons.Paused
            : status switch
            {
                SourceStatus.Connected => TrayIcons.Idle,
                SourceStatus.AuthFailed or SourceStatus.NotConfigured => TrayIcons.Problem,
                _ => TrayIcons.Paused,
            };

        var latest = _history.Latest();
        var suffix = latest is null ? "" : $", last code {latest.Code}";
        _trayIcon.Text = Truncate(_paused ? "SyncOTP, paused" : $"SyncOTP{suffix}");
    }

    /// <summary>NotifyIcon.Text throws above 63 characters.</summary>
    private static string Truncate(string text) => text.Length <= 63 ? text : text[..60] + "…";

    private void ExitApp()
    {
        FileLog.Info("exiting");
        StopSource();

        // Leaving a code on the clipboard after the app is gone would defeat the auto-clear.
        _clipboard.ClearIfUnchanged();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Notifier.Cleanup();

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconResetTimer.Dispose();
            _menu.Dispose();
            _uiThread.Dispose();
        }

        base.Dispose(disposing);
    }
}
