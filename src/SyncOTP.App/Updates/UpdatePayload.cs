using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using SyncOTP.Core;

namespace SyncOTP.App.Updates;

/// <summary>Why a downloaded update was refused. The message is shown to the user and logged.</summary>
public sealed class UpdateRejectedException(string message) : Exception(message);

/// <summary>An update that has passed every check and is ready to be swapped in.</summary>
public sealed record StagedUpdate(string Directory, string ExePath, string Version);

/// <summary>
/// Everything between "we have a zip" and "we are willing to overwrite the install". Nothing here
/// trusts the download, including the parts we produced ourselves: controlling the producer is not
/// a reason to skip a containment check, and a release can always have the wrong file attached.
/// </summary>
public static class UpdatePayload
{
    private const string ExeName = "SyncOTP.exe";

    // A framework-dependent zip is around 7 MB and a self-contained one around 55 MB. The band is
    // wide enough to survive real growth and narrow enough to catch an HTML error page.
    private const long MinZipBytes = 1_000_000;
    private const long MaxZipBytes = 250_000_000;
    private const long MaxExtractedBytes = 600_000_000;

    private static readonly TimeSpan SmokeTestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public static string StagingRoot => Path.Combine(Paths.UpdateDir, "staging");

    /// <summary>
    /// Verifies a downloaded zip and stages its contents. Throws
    /// <see cref="UpdateRejectedException"/> with a reason a human can act on, and leaves nothing
    /// behind when it does.
    /// </summary>
    /// <param name="sha256Sums">
    /// Contents of the release's SHA256SUMS.txt, or null when the release has none. Releases
    /// published before that file existed are still installable.
    /// </param>
    public static StagedUpdate Prepare(string zipPath, ReleaseAsset asset, string releaseVersion,
        bool selfContained, string? sha256Sums)
    {
        var staging = Path.Combine(StagingRoot, releaseVersion);

        try
        {
            VerifySize(zipPath, asset);
            VerifyChecksum(zipPath, asset, sha256Sums);
            VerifyStructure(zipPath);

            Extract(zipPath, staging);

            var exePath = Path.Combine(staging, ExeName);
            VerifyFlavor(staging, selfContained);
            VerifyVersion(exePath, releaseVersion);
            VerifyItStarts(exePath, releaseVersion);

            FileLog.Info($"update payload v{releaseVersion} verified and staged");
            return new StagedUpdate(staging, exePath, releaseVersion);
        }
        catch
        {
            Discard(staging);
            throw;
        }
    }

    // ---- 1. size ----------------------------------------------------------------------------

    private static void VerifySize(string zipPath, ReleaseAsset asset)
    {
        var actual = new FileInfo(zipPath).Length;

        if (asset.Size > 0 && actual != asset.Size)
            throw new UpdateRejectedException($"the download is {actual} bytes, the release says {asset.Size}");

        if (actual is < MinZipBytes or > MaxZipBytes)
            throw new UpdateRejectedException($"the download is {actual} bytes, which is not a plausible release");
    }

    // ---- 2. checksum ------------------------------------------------------------------------

    private static void VerifyChecksum(string zipPath, ReleaseAsset asset, string? sha256Sums)
    {
        if (string.IsNullOrWhiteSpace(sha256Sums))
        {
            // Releases predating the workflow change carry no sums, and refusing those would mean
            // refusing every update until the next release.
            FileLog.Info("the release has no SHA256SUMS.txt, skipping the checksum check");
            return;
        }

        var expected = FindSum(sha256Sums, asset.Name);
        if (expected is null)
        {
            FileLog.Info($"SHA256SUMS.txt has no line for {asset.Name}, skipping the checksum check");
            return;
        }

        using var stream = File.OpenRead(zipPath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new UpdateRejectedException($"the download does not match the published checksum for {asset.Name}");

        FileLog.Debug($"checksum verified for {asset.Name}");
    }

    /// <summary>Parses one "&lt;hash&gt;  &lt;filename&gt;" line out of a sums file.</summary>
    private static string? FindSum(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            // The filename half may carry a leading "*" for binary mode.
            var named = parts[^1].TrimStart('*');
            if (string.Equals(named, fileName, StringComparison.OrdinalIgnoreCase)) return parts[0];
        }

        return null;
    }

    // ---- 3. zip structure -------------------------------------------------------------------

    private static void VerifyStructure(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var total = 0L;
        var hasExe = false;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;

            if (name.Contains("..", StringComparison.Ordinal) ||
                name.StartsWith('/') || name.StartsWith('\\') ||
                Path.IsPathRooted(name) ||
                (name.Length > 1 && name[1] == ':'))
            {
                throw new UpdateRejectedException($"the archive contains an unsafe path: {name}");
            }

            total += entry.Length;
            if (total > MaxExtractedBytes)
                throw new UpdateRejectedException("the archive expands to an implausible size");

            // Compress-Archive -Path <dir>\* puts the contents at the root, with no prefix. A
            // prefixed exe means the workflow's glob changed and nothing here would work.
            if (string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase)) hasExe = true;
        }

        if (!hasExe)
            throw new UpdateRejectedException($"the archive has no {ExeName} at its root");
    }

    // ---- 4. extract -------------------------------------------------------------------------

    private static void Extract(string zipPath, string staging)
    {
        Discard(staging);
        Directory.CreateDirectory(staging);

        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));

            // Belt and braces over the structure check: this is the one that cannot be fooled by
            // an encoding trick, because it compares resolved paths.
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UpdateRejectedException($"the archive tried to write outside the staging directory: {entry.FullName}");

            // A directory entry has an empty name.
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    // ---- 5. flavour sanity --------------------------------------------------------------------

    /// <summary>
    /// The payload says which flavour it is, in the same release.json the install directory carries.
    /// There is no file to probe for instead: a single-file self-contained publish bundles the
    /// runtime into the exe rather than leaving coreclr.dll beside it, so the two flavours are
    /// indistinguishable on disk apart from the size of SyncOTP.exe.
    /// </summary>
    private static void VerifyFlavor(string staging, bool selfContained)
    {
        if (!InstallMarker.TryRead(staging, out var marker))
        {
            // Releases published before release.json existed carry no flavour, and guessing is
            // worse than trusting the asset name we picked it by.
            FileLog.Info("the payload has no release.json, skipping the flavour check");
            return;
        }

        if (marker.IsSelfContained != selfContained)
        {
            throw new UpdateRejectedException(
                $"the download says it is {marker.Flavor}, which is not what this install needs");
        }

        FileLog.Debug($"payload flavour {marker.Flavor} confirmed");
    }

    // ---- 6. the extracted exe is the version it claims ----------------------------------------

    private static void VerifyVersion(string exePath, string releaseVersion)
    {
        if (!File.Exists(exePath))
            throw new UpdateRejectedException($"{ExeName} is missing after extraction");

        var product = FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "";
        var plus = product.IndexOf('+');
        if (plus >= 0) product = product[..plus];

        if (!ReleaseVersion.TryParse(product, out var staged) ||
            !ReleaseVersion.TryParse(releaseVersion, out var expected) ||
            staged.CompareTo(expected) != 0)
        {
            throw new UpdateRejectedException(
                $"the downloaded {ExeName} reports version {product}, the release says {releaseVersion}");
        }

        if (!ReleaseVersion.IsNewer(product, AppVersion.Display))
            throw new UpdateRejectedException($"the downloaded {ExeName} is not newer than v{AppVersion.Display}");
    }

    // ---- 7. it actually starts on this machine ------------------------------------------------

    /// <summary>
    /// The only check that tests the machine rather than the bytes. A framework-dependent build
    /// landing where the installed runtime is too old fails here, which turns the worst failure in
    /// the feature into a declined update instead of a tray icon that never comes back.
    /// </summary>
    private static void VerifyItStarts(string exePath, string releaseVersion)
    {
        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(info)
                ?? throw new UpdateRejectedException($"the downloaded {ExeName} could not be started");

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit((int)SmokeTestTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new UpdateRejectedException($"the downloaded {ExeName} did not respond to --version");
            }

            if (process.ExitCode != 0)
            {
                throw new UpdateRejectedException(
                    $"the downloaded {ExeName} exited with code {process.ExitCode}. " +
                    "It may need a newer .NET runtime than this machine has.");
            }

            var reported = output.Trim();

            // A build older than the one that introduced --version treats it as an unknown
            // argument and starts normally, which means it takes the single-instance path and
            // prints nothing. Updates only ever move forward, so this should not happen in
            // practice, and refusing is the right answer when it does.
            if (reported.Length == 0)
            {
                throw new UpdateRejectedException(
                    $"the downloaded {ExeName} did not report a version. It may be an older build " +
                    "than this one, which cannot be verified and will not be installed.");
            }

            if (!ReleaseVersion.TryParse(reported, out var running) ||
                !ReleaseVersion.TryParse(releaseVersion, out var expected) ||
                running.CompareTo(expected) != 0)
            {
                throw new UpdateRejectedException($"the downloaded {ExeName} reported version \"{reported}\"");
            }

            FileLog.Debug($"the staged {ExeName} starts and reports {reported}");
        }
        catch (UpdateRejectedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UpdateRejectedException($"the downloaded {ExeName} could not be started: {ex.Message}");
        }
    }

    // ---- staging lifecycle ---------------------------------------------------------------------

    /// <summary>
    /// At startup, adopt a staged update that is still valid so "Exit without applying" costs
    /// nothing and no network call is needed to offer it again. Anything else is swept up.
    /// </summary>
    public static StagedUpdate? ResumeOrClean()
    {
        StagedUpdate? resumable = null;

        try
        {
            if (Directory.Exists(StagingRoot))
            {
                foreach (var directory in Directory.GetDirectories(StagingRoot))
                {
                    var version = Path.GetFileName(directory);
                    var exePath = Path.Combine(directory, ExeName);

                    if (resumable is null &&
                        File.Exists(exePath) &&
                        ReleaseVersion.IsNewer(version, AppVersion.Display))
                    {
                        resumable = new StagedUpdate(directory, exePath, version);
                        FileLog.Info($"a staged update to v{version} is still pending");
                        continue;
                    }

                    Discard(directory);
                }
            }

            CleanStaleFiles();
        }
        catch (Exception ex)
        {
            FileLog.Debug($"could not tidy the update staging area: {ex.Message}");
        }

        return resumable;
    }

    /// <summary>Removes downloads and leftovers older than a week. The backup is never touched.</summary>
    private static void CleanStaleFiles()
    {
        if (!Directory.Exists(Paths.UpdateDir)) return;

        var cutoff = DateTime.UtcNow - StaleAfter;

        foreach (var file in Directory.GetFiles(Paths.UpdateDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch (Exception ex)
            {
                FileLog.Debug($"could not delete {file}: {ex.Message}");
            }
        }
    }

    public static void Discard(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            FileLog.Debug($"could not remove {directory}: {ex.Message}");
        }
    }
}
