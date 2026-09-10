using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SyncOTP.App.Updates;
using SyncOTP.Core;
using Xunit;

namespace SyncOTP.App.Tests;

/// <summary>
/// Covers the checks that stand between a download and overwriting a working install. Every case
/// here is a rejection, because a rejection is the outcome that protects the user, and none of them
/// need a network or a real SyncOTP build to reach.
/// </summary>
public class UpdatePayloadTests : IDisposable
{
    // Distinctive so a leftover is obviously test debris, and never newer than a real release.
    private const string TestVersion = "99.9.9-test";

    private readonly string _work = Path.Combine(Path.GetTempPath(),
        "syncotp-payload-" + Guid.NewGuid().ToString("n"));

    public UpdatePayloadTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { }
        UpdatePayload.Discard(Path.Combine(UpdatePayload.StagingRoot, TestVersion));
    }

    /// <summary>
    /// Builds a zip over the 1 MB floor. The padding is random because compressible padding would
    /// leave the archive itself too small to reach the checks under test.
    /// </summary>
    private string MakeZip(string name, params (string EntryName, byte[] Content)[] entries)
    {
        var path = Path.Combine(_work, name);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (entryName, content) in entries)
            {
                using var stream = archive.CreateEntry(entryName).Open();
                stream.Write(content);
            }

            var padding = RandomNumberGenerator.GetBytes(1_400_000);
            using var pad = archive.CreateEntry("padding.bin").Open();
            pad.Write(padding);
        }

        return path;
    }

    private static ReleaseAsset AssetFor(string path) =>
        new(Path.GetFileName(path), "https://example.invalid/x.zip", new FileInfo(path).Length);

    private static UpdateRejectedException Rejects(string zip, ReleaseAsset asset, string? sums = null) =>
        Assert.Throws<UpdateRejectedException>(() =>
            UpdatePayload.Prepare(zip, asset, TestVersion, selfContained: false, sums));

    [Fact]
    public void AZipWithNoExeAtItsRootIsRejected()
    {
        var zip = MakeZip("no-exe.zip", ("readme.txt", "nothing here"u8.ToArray()));

        Assert.Contains("SyncOTP.exe", Rejects(zip, AssetFor(zip)).Message);
    }

    [Fact]
    public void AnExeInASubdirectoryIsRejected()
    {
        // The regression test for release.yml's "Compress-Archive -Path ...\*" glob. Dropping the
        // trailing \* would prefix every entry with a directory, and the applier would then copy a
        // folder into the install directory instead of replacing the exe.
        var zip = MakeZip("nested.zip", ("SyncOTP/SyncOTP.exe", "fake"u8.ToArray()));

        Assert.Contains("SyncOTP.exe", Rejects(zip, AssetFor(zip)).Message);
    }

    [Fact]
    public void AnEntryThatEscapesTheStagingDirectoryIsRejected()
    {
        // We produce these zips ourselves, which is not a reason to skip the check.
        var zip = MakeZip("slip.zip",
            ("SyncOTP.exe", "fake"u8.ToArray()),
            ("../escaped.txt", "gotcha"u8.ToArray()));

        Assert.Contains("unsafe path", Rejects(zip, AssetFor(zip)).Message);
    }

    [Fact]
    public void ADownloadThatDisagreesWithTheReleaseSizeIsRejected()
    {
        var zip = MakeZip("size.zip", ("SyncOTP.exe", "fake"u8.ToArray()));
        var wrong = AssetFor(zip) with { Size = new FileInfo(zip).Length + 1 };

        Assert.Contains("the release says", Rejects(zip, wrong).Message);
    }

    [Fact]
    public void AnImplausiblySmallDownloadIsRejected()
    {
        // An HTML error page served with a .zip name lands here.
        var path = Path.Combine(_work, "tiny.zip");
        File.WriteAllText(path, "<html>404</html>");

        Assert.Contains("plausible", Rejects(path, AssetFor(path)).Message);
    }

    [Fact]
    public void AChecksumMismatchIsRejected()
    {
        var zip = MakeZip("sums.zip", ("SyncOTP.exe", "fake"u8.ToArray()));
        var asset = AssetFor(zip);
        var sums = $"{new string('a', 64)}  {asset.Name}\n";

        Assert.Contains("checksum", Rejects(zip, asset, sums).Message);
    }

    [Fact]
    public void AMatchingChecksumGetsPastTheChecksumCheck()
    {
        var zip = MakeZip("good-sums.zip", ("readme.txt", "nothing here"u8.ToArray()));
        var asset = AssetFor(zip);

        using var stream = File.OpenRead(zip);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var sums = $"{hash}  {asset.Name}\n";

        // It still fails, but on the missing exe rather than the checksum, which is what proves the
        // hash was computed and compared the same way the workflow writes it.
        Assert.Contains("SyncOTP.exe", Rejects(zip, asset, sums).Message);
    }

    [Fact]
    public void SumsForOtherFilesAreIgnoredRatherThanFailing()
    {
        var zip = MakeZip("other-sums.zip", ("readme.txt", "nothing here"u8.ToArray()));
        var asset = AssetFor(zip);
        var sums = $"{new string('b', 64)}  some-other-release.zip\n";

        Assert.Contains("SyncOTP.exe", Rejects(zip, asset, sums).Message);
    }

    [Fact]
    public void APayloadForTheOtherFlavourIsRejected()
    {
        // The two flavours are indistinguishable on disk under PublishSingleFile: a self-contained
        // build bundles the runtime into the exe rather than leaving coreclr.dll beside it. The
        // release.json the build writes is the only thing that can answer this.
        var zip = MakeZip("wrong-flavour.zip",
            ("SyncOTP.exe", "fake"u8.ToArray()),
            ("release.json", Encoding.UTF8.GetBytes(
                "{ \"flavor\": \"selfcontained\", \"version\": \"99.9.9-test\" }")));

        var rejection = Assert.Throws<UpdateRejectedException>(() =>
            UpdatePayload.Prepare(zip, AssetFor(zip), TestVersion, selfContained: false, null));

        Assert.Contains("selfcontained", rejection.Message);
    }

    [Fact]
    public void APayloadWithNoMarkerSkipsTheFlavourCheck()
    {
        // Releases published before release.json existed are still installable, so a missing
        // marker has to fall through to the later checks rather than fail here.
        var zip = MakeZip("no-marker.zip", ("SyncOTP.exe", "fake"u8.ToArray()));

        var rejection = Rejects(zip, AssetFor(zip));

        Assert.DoesNotContain("flavour", rejection.Message);
    }

    [Fact]
    public void ARejectedPayloadLeavesNothingStaged()
    {
        var zip = MakeZip("cleanup.zip", ("readme.txt", "nothing here"u8.ToArray()));

        Rejects(zip, AssetFor(zip));

        Assert.False(Directory.Exists(Path.Combine(UpdatePayload.StagingRoot, TestVersion)));
    }
}
