namespace SyncOTP.Core;

/// <summary>One downloadable file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>
/// A published release, in a shape that says nothing about where it came from. The client that
/// talks to the release host owns its own wire-format type and maps into this, the way
/// <c>NtfySource</c> keeps its <c>NtfyEvent</c> private, so this stays trivial to build in tests.
/// </summary>
public sealed record ReleaseInfo(
    string TagName,
    string Version,
    string HtmlUrl,
    bool PreRelease,
    IReadOnlyList<ReleaseAsset> Assets);
