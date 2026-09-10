using System.Diagnostics;
using SyncOTP.Core;

namespace SyncOTP.App.Updates;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Blocked,
    Failed,
}

/// <summary>Which phase failed, so the tray can open the log that actually has the detail in it.</summary>
public enum UpdatePhase { None, Check, Download, Apply }

public sealed record UpdateState(
    UpdateStatus Status,
    string? Version = null,
    string? ReleaseUrl = null,
    double Progress = 0,
    string? Detail = null,
    UpdatePhase FailedPhase = UpdatePhase.None);

/// <summary>
/// The one type the tray talks to about updates. Owns the HTTP client, the staging area and the
/// state machine, and marshals every state change onto the UI thread itself so callers cannot
/// forget to.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan ManualCheckDebounce = TimeSpan.FromSeconds(60);

    private readonly Control _uiThread;
    private readonly GitHubReleaseClient _client = new();
    private readonly InstallLocation _install;

    private Config _config;
    private CancellationTokenSource _cts = new();
    private DateTimeOffset _lastManualCheck = DateTimeOffset.MinValue;
    private bool _blockedReasonLogged;
    private bool _busy;

    private ReleaseInfo? _release;
    private ReleaseAsset? _asset;
    private StagedUpdate? _staged;

    public UpdateState State { get; private set; } = new(UpdateStatus.Idle);

    /// <summary>Always raised on the UI thread.</summary>
    public event Action<UpdateState>? StateChanged;

    public UpdateService(Control uiThread, Config config)
    {
        _uiThread = uiThread;
        _config = config;
        _install = InstallLocation.Detect();

        FileLog.Info($"install: {_install.Directory} " +
                     $"({(_install.SelfContained ? "self-contained" : "framework-dependent")})");

        // A leftover from a previous session is worth adopting: it means the user can restart into
        // the new version without going near the network again.
        _staged = UpdatePayload.ResumeOrClean();
        if (_staged is not null)
        {
            State = new UpdateState(UpdateStatus.ReadyToRestart, _staged.Version);
        }
    }

    public void ApplyConfig(Config config) => _config = config;

    /// <summary>True when the schedule says an automatic check is due.</summary>
    public bool IsCheckDue(DateTimeOffset nowUtc) =>
        UpdateSchedule.IsDue(_config.Updates.LastCheck, nowUtc, _config.Updates.Interval);

    public TimeSpan NextDelay(DateTimeOffset nowUtc, bool firstEvaluation) =>
        UpdateSchedule.NextDelay(_config.Updates.LastCheck, nowUtc, _config.Updates.Interval, firstEvaluation);

    // ---- check -----------------------------------------------------------------------------

    public async Task CheckAsync(bool userInitiated)
    {
        // A staged update is already the answer, and checking again would only confuse the menu.
        if (_staged is not null) return;
        if (_busy) return;

        if (userInitiated && DateTimeOffset.UtcNow - _lastManualCheck < ManualCheckDebounce)
        {
            FileLog.Debug("manual update check ignored, one just ran");
            return;
        }

        _busy = true;
        if (userInitiated) _lastManualCheck = DateTimeOffset.UtcNow;

        try
        {
            if (userInitiated) Publish(new UpdateState(UpdateStatus.Checking));

            var release = await _client
                .GetLatestAsync(_config.Updates.AllowPreRelease, _cts.Token)
                .ConfigureAwait(false);

            // Null means "we could not find out", never "you are up to date", so the timestamp is
            // deliberately not recorded and the next tick tries again.
            if (release is null)
            {
                Publish(userInitiated
                    ? new UpdateState(UpdateStatus.Failed, Detail: "Could not reach GitHub.",
                        FailedPhase: UpdatePhase.Check)
                    : new UpdateState(UpdateStatus.Idle));
                return;
            }

            _config.SaveUpdateState(DateTimeOffset.UtcNow, release.Version);

            if (!ReleaseVersion.IsNewer(release.Version, AppVersion.Display))
            {
                FileLog.Debug($"update check: v{release.Version} is not newer than v{AppVersion.Display}");
                Publish(new UpdateState(userInitiated ? UpdateStatus.UpToDate : UpdateStatus.Idle));
                return;
            }

            var asset = ReleaseAssetPicker.Pick(release, _install.SelfContained);
            if (asset is null)
            {
                var flavor = _install.SelfContained ? "self-contained" : "framework-dependent";
                FileLog.Warn($"release v{release.Version} has no {flavor} asset");
                Publish(new UpdateState(UpdateStatus.Blocked, release.Version, release.HtmlUrl,
                    Detail: $"Release v{release.Version} has no {flavor} download."));
                return;
            }

            _release = release;
            _asset = asset;

            FileLog.Info($"update available: v{release.Version}");

            if (!_install.CanSelfUpdate)
            {
                // Once per session, not once per check: this never changes while the app runs.
                if (!_blockedReasonLogged)
                {
                    FileLog.Info($"self-update is unavailable, {_install.BlockedReason}");
                    _blockedReasonLogged = true;
                }

                Publish(new UpdateState(UpdateStatus.Blocked, release.Version, release.HtmlUrl,
                    Detail: _install.BlockedReason));
                return;
            }

            Publish(new UpdateState(UpdateStatus.Available, release.Version, release.HtmlUrl));
        }
        finally
        {
            _busy = false;
        }

        if (_config.Updates.DownloadAutomatically && State.Status == UpdateStatus.Available)
            await DownloadAsync().ConfigureAwait(false);
    }

    // ---- download --------------------------------------------------------------------------

    public async Task DownloadAsync()
    {
        if (_busy || _release is null || _asset is null || !_install.CanSelfUpdate) return;

        _busy = true;
        var release = _release;
        var asset = _asset;
        var zipPath = Path.Combine(Paths.UpdateDir, asset.Name);

        try
        {
            Publish(new UpdateState(UpdateStatus.Downloading, release.Version, release.HtmlUrl));

            var progress = new Progress<double>(fraction => Publish(
                new UpdateState(UpdateStatus.Downloading, release.Version, release.HtmlUrl, fraction)));

            Directory.CreateDirectory(Paths.UpdateDir);
            await _client.DownloadAsync(asset, zipPath, progress, _cts.Token).ConfigureAwait(false);

            var sums = await FetchChecksumsAsync(release).ConfigureAwait(false);

            var staged = UpdatePayload.Prepare(zipPath, asset, release.Version,
                _install.SelfContained, sums);

            _staged = staged;
            Publish(new UpdateState(UpdateStatus.ReadyToRestart, staged.Version, release.HtmlUrl));
        }
        catch (OperationCanceledException)
        {
            FileLog.Debug("update download cancelled");
            Publish(new UpdateState(UpdateStatus.Available, release.Version, release.HtmlUrl));
        }
        catch (UpdateRejectedException ex)
        {
            FileLog.Error($"update to v{release.Version} refused: {ex.Message}");
            Publish(new UpdateState(UpdateStatus.Failed, release.Version, release.HtmlUrl,
                Detail: ex.Message, FailedPhase: UpdatePhase.Download));
        }
        catch (Exception ex)
        {
            FileLog.Error($"update to v{release.Version} failed", ex);
            Publish(new UpdateState(UpdateStatus.Failed, release.Version, release.HtmlUrl,
                Detail: ex.Message, FailedPhase: UpdatePhase.Download));
        }
        finally
        {
            // The zip has served its purpose either way, and it is the largest thing we downloaded.
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            _busy = false;
        }
    }

    private async Task<string?> FetchChecksumsAsync(ReleaseInfo release)
    {
        var sums = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

        return sums is null
            ? null
            : await _client.TryGetTextAssetAsync(sums, _cts.Token).ConfigureAwait(false);
    }

    // ---- apply -----------------------------------------------------------------------------

    /// <summary>
    /// Starts the applier and reports whether the caller should now exit. The applier waits for
    /// this process to go away before it touches anything, so a false here means nothing has
    /// changed and the app should carry on running.
    /// </summary>
    public bool LaunchApplier()
    {
        if (_staged is null || !_install.CanSelfUpdate) return false;

        try
        {
            var script = WriteApplierScript(_staged.Directory);

            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Paths.UpdateDir,
            };

            // -ExecutionPolicy Bypass because the script was just written and is unsigned.
            // -NoProfile so a user profile cannot change $ErrorActionPreference underneath it.
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-WindowStyle");
            info.ArgumentList.Add("Hidden");
            info.ArgumentList.Add("-File");
            info.ArgumentList.Add(script);
            info.ArgumentList.Add("-Source");
            info.ArgumentList.Add(_staged.Directory);
            info.ArgumentList.Add("-Target");
            info.ArgumentList.Add(_install.Directory);
            info.ArgumentList.Add("-Exe");
            info.ArgumentList.Add(_install.ExePath);
            info.ArgumentList.Add("-ProcessId");
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            info.ArgumentList.Add("-Backup");
            info.ArgumentList.Add(Path.Combine(Paths.UpdateDir, "backup"));
            info.ArgumentList.Add("-Log");
            info.ArgumentList.Add(Paths.UpdateLogFile);
            info.ArgumentList.Add("-RemoveSource");
            info.ArgumentList.Add("-RelaunchArgs");
            info.ArgumentList.Add("--updated");

            var process = Process.Start(info);
            if (process is null)
            {
                FileLog.Error("could not start the update applier");
                return false;
            }

            FileLog.Info($"applier started (pid {process.Id}), applying v{_staged.Version}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Error("could not start the update applier", ex);
            Publish(new UpdateState(UpdateStatus.Failed, _staged.Version,
                Detail: ex.Message, FailedPhase: UpdatePhase.Apply));
            return false;
        }
    }

    /// <summary>
    /// Copies the applier out of the payload we just verified, so the script that runs always
    /// matches the release it is installing. Falls back to the one beside the running exe for a
    /// payload that predates it.
    /// </summary>
    private static string WriteApplierScript(string stagedDirectory)
    {
        const string name = "apply-payload.ps1";
        var destination = Path.Combine(Paths.UpdateDir, name);

        foreach (var candidate in CandidateScripts(name, stagedDirectory))
        {
            if (!File.Exists(candidate)) continue;

            File.Copy(candidate, destination, overwrite: true);
            return destination;
        }

        throw new FileNotFoundException($"{name} was not found beside the application");
    }

    private static IEnumerable<string> CandidateScripts(string name, string stagedDirectory)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        // The payload's own copy first: it is the applier that release was tested with.
        yield return Path.Combine(stagedDirectory, name);
        yield return Path.Combine(exeDir, name);
        yield return Path.Combine(AppContext.BaseDirectory, name);

        // Running from a clone, where the script is still only in scripts/.
        var repoRoot = Directory.GetParent(exeDir)?.FullName;
        for (var i = 0; i < 6 && repoRoot is not null; i++)
        {
            yield return Path.Combine(repoRoot, "scripts", name);
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
    }

    public void OpenReleasePage()
    {
        var url = State.ReleaseUrl ?? "https://github.com/msitt/SyncOTP/releases";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Error("could not open the release page", ex);
        }
    }

    public void CancelPendingWork()
    {
        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Debug($"cancelling update work: {ex.Message}");
        }

        _cts = new CancellationTokenSource();
    }

    private void Publish(UpdateState state)
    {
        State = state;

        try
        {
            if (_uiThread.IsHandleCreated && !_uiThread.IsDisposed)
                _uiThread.BeginInvoke(() => StateChanged?.Invoke(state));
        }
        catch (Exception ex)
        {
            FileLog.Debug($"could not publish update state: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _client.Dispose();
    }
}
