using SyncOTP.Core;

namespace SyncOTP.App;

/// <summary>
/// One place that knows how SyncOTP identifies itself over HTTP. The version belongs in the
/// User-Agent so a server operator can tell which build is talking to them, and GitHub rejects
/// requests that carry no User-Agent at all.
/// </summary>
internal static class HttpClients
{
    public static string UserAgent { get; } = $"SyncOTP/{AppVersion.Display}";

    public static HttpClient Create(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// For a response the caller reads as a long-lived stream, where a client timeout would cut off
    /// a connection that is working exactly as intended. Callers bound individual requests with a
    /// linked CancellationTokenSource instead.
    /// </summary>
    public static HttpClient CreateStreaming() => Create(Timeout.InfiniteTimeSpan);
}
