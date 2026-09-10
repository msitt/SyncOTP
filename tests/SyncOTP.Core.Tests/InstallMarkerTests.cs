using SyncOTP.Core;
using Xunit;

namespace SyncOTP.Core.Tests;

public class InstallMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "syncotp-marker-" + Guid.NewGuid().ToString("n"));

    public InstallMarkerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void WriteMarker(string json) =>
        File.WriteAllText(Path.Combine(_dir, InstallMarker.FileName), json);

    [Fact]
    public void ASelfContainedMarkerIsRead()
    {
        WriteMarker("""{ "flavor": "selfcontained", "version": "0.2.0", "installedBy": "release" }""");

        Assert.True(InstallMarker.TryRead(_dir, out var marker));
        Assert.True(marker.IsSelfContained);
        Assert.Equal("0.2.0", marker.Version);
        Assert.Equal("release", marker.InstalledBy);
    }

    [Fact]
    public void AFrameworkDependentMarkerIsRead()
    {
        WriteMarker("""{ "flavor": "framework-dependent", "version": "0.2.0", "installedBy": "local-build" }""");

        Assert.True(InstallMarker.TryRead(_dir, out var marker));
        Assert.False(marker.IsSelfContained);
        Assert.True(marker.HasKnownFlavor);
    }

    [Fact]
    public void TheFlavorIsMatchedIgnoringCase()
    {
        WriteMarker("""{ "flavor": "SelfContained", "version": "0.2.0" }""");

        Assert.True(InstallMarker.TryRead(_dir, out var marker));
        Assert.True(marker.IsSelfContained);
    }

    [Fact]
    public void AMissingFileIsNotAManagedInstall()
    {
        // A build output directory, or an install predating the marker. Both are normal.
        Assert.False(InstallMarker.TryRead(_dir, out _));
    }

    [Fact]
    public void AnUnknownFlavorIsRejected()
    {
        // A marker we cannot interpret is worth no more than no marker, so the caller falls back
        // to the build-time stamp rather than guessing at a flavor it has never heard of.
        WriteMarker("""{ "flavor": "linux-musl", "version": "0.2.0" }""");

        Assert.False(InstallMarker.TryRead(_dir, out _));
    }

    [Fact]
    public void MalformedJsonIsRejectedRatherThanThrowing()
    {
        WriteMarker("{ this is not json");

        Assert.False(InstallMarker.TryRead(_dir, out _));
    }

    [Fact]
    public void AnEmptyOrMissingDirectoryIsRejected()
    {
        Assert.False(InstallMarker.TryRead("", out _));
        Assert.False(InstallMarker.TryRead(null, out _));
        Assert.False(InstallMarker.TryRead(Path.Combine(_dir, "nope"), out _));
    }

    [Fact]
    public void TheFlavorConstantsMatchTheAssetSuffixes()
    {
        // The marker and the asset name have to agree, or the updater downloads the wrong zip.
        Assert.EndsWith(InstallMarker.SelfContainedFlavor + ".zip",
            ReleaseAssetPicker.SelfContainedSuffix, StringComparison.Ordinal);
        Assert.EndsWith(InstallMarker.FrameworkDependentFlavor + ".zip",
            ReleaseAssetPicker.FrameworkDependentSuffix, StringComparison.Ordinal);
    }
}
