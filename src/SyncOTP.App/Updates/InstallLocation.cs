using System.Reflection;
using SyncOTP.Core;

namespace SyncOTP.App.Updates;

/// <summary>
/// Where this copy of SyncOTP is installed, which published flavor it is, and whether it is
/// allowed to replace itself. Everything the updater needs to know about its own install before
/// it touches the network.
/// </summary>
public sealed record InstallLocation(
    string ExePath,
    string Directory,
    bool SelfContained,
    bool CanSelfUpdate,
    string? BlockedReason)
{
    /// <summary>Written and removed by the write probe, so it must not collide with anything real.</summary>
    private const string ProbeFileName = ".syncotp-update-probe";

    public static InstallLocation Detect()
    {
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SyncOTP.exe");
        var directory = Path.GetDirectoryName(exePath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return new InstallLocation(exePath, AppContext.BaseDirectory, DetectFlavor(null),
                false, "the install directory could not be determined");
        }

        var selfContained = DetectFlavor(directory);
        var blocked = FindBlocker(directory);

        return new InstallLocation(exePath, directory, selfContained, blocked is null, blocked);
    }

    /// <summary>
    /// The marker the build wrote is the truth. The compiled-in stamp is the fallback for an
    /// install that predates the marker, or a plain <c>dotnet run</c>.
    /// </summary>
    private static bool DetectFlavor(string? directory)
    {
        var stamped = StampedSelfContained();

        if (InstallMarker.TryRead(directory, out var marker))
        {
            if (stamped is { } value && value != marker.IsSelfContained)
            {
                FileLog.Warn($"release.json says flavor={marker.Flavor} but this build was stamped " +
                             $"SelfContained={value}. Trusting release.json.");
            }

            return marker.IsSelfContained;
        }

        return stamped ?? false;
    }

    /// <summary>
    /// The value of the MSBuild SelfContained property at build time, baked in by an
    /// AssemblyMetadata item. Null when the attribute is missing or is not a bool.
    /// </summary>
    private static bool? StampedSelfContained()
    {
        try
        {
            var value = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "SelfContained")?.Value;

            return bool.TryParse(value, out var parsed) ? parsed : null;
        }
        catch (Exception ex)
        {
            FileLog.Debug($"could not read the SelfContained stamp: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Why this install must not replace itself, or null when it may. Ordered cheapest first, with
    /// the write probe last because it is the only one that touches the disk. The path checks are
    /// heuristics, the probe is ground truth, and both matter: a developer's build output is
    /// perfectly writable and still the wrong thing to overwrite.
    /// </summary>
    private static string? FindBlocker(string directory)
    {
        if (HasSegment(directory, "bin", "Debug") || HasSegment(directory, "bin", "Release"))
            return "SyncOTP is running from a build output directory";

        if (HasSegment(directory, "artifacts"))
            return "SyncOTP is running from the build artifacts directory";

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.Windows,
                 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root) && IsUnder(directory, root))
                return "SyncOTP is installed for all users and updating it needs an administrator";
        }

        return CanWriteTo(directory) ? null : "the install directory is not writable";
    }

    private static bool HasSegment(string directory, params string[] consecutive)
    {
        var parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i + consecutive.Length <= parts.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < consecutive.Length; j++)
            {
                if (!string.Equals(parts[i + j], consecutive[j], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched) return true;
        }

        return false;
    }

    private static bool IsUnder(string directory, string root)
    {
        try
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

            return full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ACLs, read-only volumes and controlled-folder-access policies are only ever settled by
    /// trying, so this creates a file rather than reasoning about permissions.
    /// </summary>
    private static bool CanWriteTo(string directory)
    {
        var probe = Path.Combine(directory, ProbeFileName);

        try
        {
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Debug($"write probe failed in {directory}: {ex.Message}");
            try { File.Delete(probe); } catch { }
            return false;
        }
    }
}
