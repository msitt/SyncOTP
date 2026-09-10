using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SyncOTP.Core;

namespace SyncOTP.App.Updates;

/// <summary>
/// Reads releases from the GitHub API and downloads their assets. Unauthenticated, because a
/// client-side app has nowhere safe to keep a token, which caps us at 60 requests an hour per IP
/// and shapes most of what follows.
///
/// Every failure here is a soft one. A laptop that is asleep, offline or behind a captive portal
/// is the normal case, not an error worth telling the user about.
/// </summary>
public sealed class GitHubReleaseClient : IDisposable
{
    private const string Repository = "msitt/SyncOTP";
    private const string LatestUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string ListUrl = $"https://api.github.com/repos/{Repository}/releases?per_page=10";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;

    /// <summary>
    /// Kept in memory, never in config.json. A 304 costs no rate-limit budget, and a cache header
    /// is not something a user should find in a file they hand-edit.
    /// </summary>
    private string? _etag;

    private ReleaseInfo? _cached;

    /// <summary>When the rate limit is exhausted, the UTC time it resets. Null while we have budget.</summary>
    private DateTimeOffset? _rateLimitedUntil;

    public GitHubReleaseClient()
    {
        _http = HttpClients.Create(ApiTimeout);
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>
    /// The newest release, or null when the check could not be completed. Null means "we do not
    /// know", never "you are up to date", so callers must not persist a check timestamp for it.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestAsync(bool allowPreRelease, CancellationToken cancellationToken)
    {
        if (_rateLimitedUntil is { } until && DateTimeOffset.UtcNow < until)
        {
            FileLog.Warn($"skipping the update check, the GitHub rate limit resets at {until:HH:mm} UTC");
            return null;
        }

        try
        {
            return allowPreRelease
                ? await GetFromListAsync(cancellationToken).ConfigureAwait(false)
                : await GetLatestOnlyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or JsonException or IOException)
        {
            // Not an Error: being unable to reach GitHub is routine and not actionable.
            FileLog.Warn($"update check failed: {ex.Message}");
            return null;
        }
    }

    private async Task<ReleaseInfo?> GetLatestOnlyAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (_etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ApiTimeout);

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

        NoteRateLimit(response);

        // Nothing changed since last time, and it cost us nothing.
        if (response.StatusCode == HttpStatusCode.NotModified && _cached is not null)
        {
            FileLog.Debug("update check: not modified");
            return _cached;
        }

        if (!response.IsSuccessStatusCode)
        {
            FileLog.Warn($"update check returned {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);
        var mapped = Map(release);

        if (mapped is not null)
        {
            _etag = response.Headers.ETag?.ToString();
            _cached = mapped;
        }

        return mapped;
    }

    /// <summary>
    /// /releases/latest deliberately skips prereleases, so opting into them means reading the list
    /// and picking the newest entry that parses.
    /// </summary>
    private async Task<ReleaseInfo?> GetFromListAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ListUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ApiTimeout);

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

        NoteRateLimit(response);

        if (!response.IsSuccessStatusCode)
        {
            FileLog.Warn($"update check returned {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonOptions);
        if (releases is null) return null;

        ReleaseInfo? best = null;
        ReleaseVersion bestVersion = default;

        foreach (var candidate in releases)
        {
            if (candidate.Draft) continue;

            var mapped = Map(candidate);
            if (mapped is null) continue;
            if (!ReleaseVersion.TryParse(mapped.Version, out var version)) continue;

            if (best is null || version.CompareTo(bestVersion) > 0)
            {
                best = mapped;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>
    /// Streams an asset to disk. Writes to a ".part" file and renames on success, so a half a
    /// download can never be mistaken for a whole one, the same discipline Config.Save uses.
    /// </summary>
    public async Task DownloadAsync(ReleaseAsset asset, string destinationPath,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var partPath = destinationPath + ".part";

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(partPath)) File.Delete(partPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.Accept.ParseAdd("application/octet-stream");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DownloadTimeout);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // Catch an obviously wrong body before writing a single byte of it.
        var declared = response.Content.Headers.ContentLength;
        if (declared is { } length && length != asset.Size)
        {
            throw new InvalidOperationException(
                $"{asset.Name} is {length} bytes, the release says {asset.Size}");
        }

        var written = 0L;
        var lastReport = DateTimeOffset.MinValue;

        await using (var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
        await using (var target = File.Create(partPath))
        {
            var buffer = new byte[81920];
            int read;

            while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                written += read;

                if (asset.Size > 0 && written > asset.Size)
                    throw new InvalidOperationException($"{asset.Name} is longer than the release says");

                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);

                // The only consumer is a menu label, so once a second is plenty.
                var now = DateTimeOffset.UtcNow;
                if (asset.Size > 0 && progress is not null && now - lastReport > TimeSpan.FromSeconds(1))
                {
                    lastReport = now;
                    progress.Report((double)written / asset.Size);
                }
            }
        }

        if (asset.Size > 0 && written != asset.Size)
            throw new InvalidOperationException($"{asset.Name} stopped after {written} of {asset.Size} bytes");

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        File.Move(partPath, destinationPath);

        progress?.Report(1);
        FileLog.Info($"downloaded {asset.Name} ({written} bytes)");
    }

    /// <summary>Fetches a small text asset, used for SHA256SUMS.txt. Null when it is not there.</summary>
    public async Task<string?> TryGetTextAssetAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.Accept.ParseAdd("application/octet-stream");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ApiTimeout);

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            FileLog.Debug($"could not fetch {asset.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the rate-limit headers and refuses to call again before the reset once the budget is
    /// gone. Hammering a 403 would be the fastest way to stay blocked.
    /// </summary>
    private void NoteRateLimit(HttpResponseMessage response)
    {
        if (!TryGetHeader(response, "X-RateLimit-Remaining", out var remaining)) return;

        if (remaining > 0)
        {
            _rateLimitedUntil = null;
            return;
        }

        if (TryGetHeader(response, "X-RateLimit-Reset", out var reset))
        {
            _rateLimitedUntil = DateTimeOffset.FromUnixTimeSeconds(reset);
            FileLog.Warn($"GitHub rate limit reached, no more checks until {_rateLimitedUntil:HH:mm} UTC");
        }
    }

    private static bool TryGetHeader(HttpResponseMessage response, string name, out long value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) &&
               long.TryParse(values.FirstOrDefault(), out value);
    }

    private static ReleaseInfo? Map(GitHubRelease? release)
    {
        if (release?.TagName is null) return null;

        var assets = new List<ReleaseAsset>();
        foreach (var asset in release.Assets ?? [])
        {
            if (asset.Name is null || asset.BrowserDownloadUrl is null) continue;
            assets.Add(new ReleaseAsset(asset.Name, asset.BrowserDownloadUrl, asset.Size));
        }

        return new ReleaseInfo(
            TagName: release.TagName,
            Version: release.TagName.TrimStart('v', 'V'),
            HtmlUrl: release.HtmlUrl ?? $"https://github.com/{Repository}/releases",
            PreRelease: release.PreRelease,
            Assets: assets);
    }

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GitHubRelease
    {
        public string? TagName { get; set; }
        public string? HtmlUrl { get; set; }
        public bool Draft { get; set; }
        public bool PreRelease { get; set; }
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        public string? Name { get; set; }
        public string? BrowserDownloadUrl { get; set; }
        public long Size { get; set; }
    }
}
