using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyncOTP.Core;

/// <summary>
/// <c>release.json</c>, written next to the exe by whatever produced the install: the release
/// workflow for a downloaded zip, <c>install.ps1</c> for a local build. It records the one fact
/// nothing else can answer reliably at runtime, which of the two published flavors this copy is,
/// and doubles as proof that the directory is a real install rather than a build output somebody
/// is running from.
/// </summary>
public sealed class InstallMarker
{
    public const string FileName = "release.json";

    /// <summary>Matches the artifact-name flavors in <see cref="ReleaseAssetPicker"/>.</summary>
    public const string SelfContainedFlavor = "selfcontained";

    public const string FrameworkDependentFlavor = "framework-dependent";

    public string Flavor { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>"release" or "local-build". Informational, for the log and for humans.</summary>
    public string InstalledBy { get; set; } = "";

    [JsonIgnore]
    public bool IsSelfContained =>
        string.Equals(Flavor, SelfContainedFlavor, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// False when the flavor is missing or is a spelling we do not recognise. A marker we cannot
    /// interpret is worth no more than no marker at all, so callers fall back rather than guess.
    /// </summary>
    [JsonIgnore]
    public bool HasKnownFlavor =>
        IsSelfContained ||
        string.Equals(Flavor, FrameworkDependentFlavor, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads the marker from an install directory. Returns false for a missing, unreadable or
    /// unrecognisable file, which callers must treat as "not a managed install" rather than as an
    /// error: an install predating the marker, or a plain <c>dotnet run</c>, both land here.
    /// </summary>
    public static bool TryRead(string? directory, out InstallMarker marker)
    {
        marker = new InstallMarker();
        if (string.IsNullOrWhiteSpace(directory)) return false;

        var path = Path.Combine(directory, FileName);

        try
        {
            if (!File.Exists(path)) return false;

            var parsed = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path), Options);
            if (parsed is null || !parsed.HasKnownFlavor) return false;

            marker = parsed;
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Debug($"could not read {path}: {ex.Message}");
            return false;
        }
    }
}
