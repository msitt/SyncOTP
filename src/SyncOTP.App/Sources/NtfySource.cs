using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SyncOTP.Core;

namespace SyncOTP.App.Sources;

/// <summary>
/// Subscribes to an ntfy topic over its newline-delimited JSON stream.
///
/// The stream is a long-lived HTTP GET: ntfy writes one JSON object per line and sends a keepalive
/// roughly every 45 s. Cloudflare Tunnel closes a proxied connection that is idle for 100 s, so the
/// keepalive is what holds it open, and a dropped connection is expected routine rather than an
/// error. Reconnects resume with <c>?since=&lt;last id&gt;</c> so nothing is lost in the gap.
/// </summary>
public sealed class NtfySource : IMessageSource
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly Config _config;
    private readonly HttpClient _http;

    public string Name => "ntfy";

    public SourceState State { get; private set; } = new(SourceStatus.Stopped);

    public event Action<IncomingMessage>? MessageReceived;
    public event Action<IMessageSource, SourceState>? StateChanged;

    /// <summary>Raised when a message id is processed, so the caller can persist the resume cursor.</summary>
    public event Action<string>? Progressed;

    public NtfySource(Config config)
    {
        _config = config;
        _http = new HttpClient
        {
            // The stream never completes on its own, so the client must not impose a timeout.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SyncOTP/1.0");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Ntfy.IsConfigured)
        {
            var detail = $"set ntfy.server and ntfy.topic in {Paths.ConfigFile}";
            FileLog.Warn($"ntfy source not started: {detail}");
            SetState(new SourceState(SourceStatus.NotConfigured, detail));
            return;
        }

        var backoff = MinBackoff;
        var firstAttempt = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(new SourceState(firstAttempt ? SourceStatus.Connecting : SourceStatus.Reconnecting,
                _config.Ntfy.TopicUrl));

            try
            {
                await StreamOnceAsync(cancellationToken).ConfigureAwait(false);

                // A clean end of stream just means the connection was recycled.
                FileLog.Debug("ntfy stream ended; reconnecting");
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (UnauthorizedException ex)
            {
                SetState(new SourceState(SourceStatus.AuthFailed, ex.Message));
                FileLog.Error($"ntfy rejected the credentials for user '{_config.Ntfy.Username}' ({ex.Message}). " +
                              "Fix the password in config.json, then use Reload config.");
                return;
            }
            catch (Exception ex)
            {
                FileLog.Warn($"ntfy connection lost: {ex.Message}");
            }

            firstAttempt = false;
            if (cancellationToken.IsCancellationRequested) break;

            SetState(new SourceState(SourceStatus.Reconnecting, $"retrying in {backoff.TotalSeconds:0}s"));

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }

        SetState(new SourceState(SourceStatus.Stopped));
    }

    private async Task StreamOnceAsync(CancellationToken cancellationToken)
    {
        // Resume from the last id we handled. On a cold start fall back to a short lookback so a
        // code that arrived while the app was launching is not missed.
        var since = string.IsNullOrWhiteSpace(_config.Ntfy.LastId) ? "10m" : _config.Ntfy.LastId;
        var url = $"{_config.Ntfy.TopicUrl}/json?since={Uri.EscapeDataString(since)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_config.Ntfy.HasCredentials)
            request.Headers.TryAddWithoutValidation("Authorization", _config.Ntfy.BuildBasicAuthHeader());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedException($"HTTP {(int)response.StatusCode} {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        SetState(new SourceState(SourceStatus.Connected, _config.Ntfy.TopicUrl));
        FileLog.Info($"ntfy connected to {_config.Ntfy.TopicUrl} (since={since})");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        // If even the keepalives stop arriving the socket is dead but not closed; tear it down
        // ourselves rather than waiting forever.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!watchdog.IsCancellationRequested)
        {
            var readLine = reader.ReadLineAsync(watchdog.Token).AsTask();
            var timeout = Task.Delay(Watchdog, watchdog.Token);

            var completed = await Task.WhenAny(readLine, timeout).ConfigureAwait(false);
            if (completed == timeout)
            {
                await watchdog.CancelAsync().ConfigureAwait(false);
                throw new TimeoutException($"no data or keepalive for {Watchdog.TotalSeconds:0}s");
            }

            var line = await readLine.ConfigureAwait(false);
            if (line is null) return; // stream closed
            if (string.IsNullOrWhiteSpace(line)) continue;

            HandleLine(line);
        }
    }

    private void HandleLine(string line)
    {
        NtfyEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<NtfyEvent>(line, JsonOptions);
        }
        catch (JsonException ex)
        {
            FileLog.Warn($"unparseable line from ntfy: {ex.Message}");
            return;
        }

        if (evt is null) return;

        // "open" and "keepalive" carry no payload; reaching here at all is what resets the watchdog.
        if (!string.Equals(evt.Event, "message", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Debug($"ntfy {evt.Event}");
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.Message)) return;

        var message = new IncomingMessage(
            Id: evt.Id ?? Guid.NewGuid().ToString("n"),
            Text: evt.Message,
            From: string.IsNullOrWhiteSpace(evt.Title) ? null : evt.Title,
            SentAt: DateTimeOffset.FromUnixTimeSeconds(evt.Time > 0 ? evt.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            SourceName: Name);

        if (evt.Id is not null) Progressed?.Invoke(evt.Id);
        MessageReceived?.Invoke(message);
    }

    /// <summary>Publishes to the topic. Used by the tray's "Send test message".</summary>
    public async Task PublishAsync(string text, string? title, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Ntfy.TopicUrl)
        {
            Content = new StringContent(text),
        };
        if (_config.Ntfy.HasCredentials)
            request.Headers.TryAddWithoutValidation("Authorization", _config.Ntfy.BuildBasicAuthHeader());
        if (!string.IsNullOrWhiteSpace(title)) request.Headers.TryAddWithoutValidation("Title", title);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private void SetState(SourceState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class NtfyEvent
    {
        public string? Id { get; set; }
        public long Time { get; set; }
        public string? Event { get; set; }
        public string? Topic { get; set; }
        public string? Message { get; set; }
        public string? Title { get; set; }
    }

    private sealed class UnauthorizedException(string message) : Exception(message);
}
