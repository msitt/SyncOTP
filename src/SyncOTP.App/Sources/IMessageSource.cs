using SyncOTP.Core;

namespace SyncOTP.App.Sources;

public enum SourceStatus
{
    Stopped,
    Connecting,
    Connected,
    Reconnecting,

    /// <summary>Credentials were rejected. Retrying will not help until config changes.</summary>
    AuthFailed,

    /// <summary>Config is incomplete, so the source never started.</summary>
    NotConfigured,
}

public sealed record SourceState(SourceStatus Status, string Detail = "");

/// <summary>
/// A way for messages to reach the app. Only <see cref="NtfySource"/> exists today; the interface
/// is here so a second transport can be added without touching the processing pipeline.
/// </summary>
public interface IMessageSource : IAsyncDisposable
{
    string Name { get; }

    SourceState State { get; }

    event Action<IncomingMessage>? MessageReceived;

    event Action<IMessageSource, SourceState>? StateChanged;

    /// <summary>Runs until the token is cancelled, reconnecting on its own as needed.</summary>
    Task StartAsync(CancellationToken cancellationToken);
}
