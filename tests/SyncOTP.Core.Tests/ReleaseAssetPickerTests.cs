using SyncOTP.Core;
using Xunit;

namespace SyncOTP.Core.Tests;

public class ReleaseAssetPickerTests
{
    private static ReleaseInfo Release(params string[] assetNames) =>
        new("v0.2.0", "0.2.0", "https://example.invalid/releases/v0.2.0", false,
            assetNames.Select(n => new ReleaseAsset(n, $"https://example.invalid/{n}", 1024)).ToList());

    private static readonly string[] BothFlavors =
    [
        "SyncOTP-0.2.0-win-x64-selfcontained.zip",
        "SyncOTP-0.2.0-win-x64-framework-dependent.zip",
    ];

    [Fact]
    public void TheSelfContainedZipIsPickedForASelfContainedInstall()
    {
        var picked = ReleaseAssetPicker.Pick(Release(BothFlavors), selfContained: true);

        Assert.Equal("SyncOTP-0.2.0-win-x64-selfcontained.zip", picked?.Name);
    }

    [Fact]
    public void TheFrameworkDependentZipIsPickedOtherwise()
    {
        var picked = ReleaseAssetPicker.Pick(Release(BothFlavors), selfContained: false);

        Assert.Equal("SyncOTP-0.2.0-win-x64-framework-dependent.zip", picked?.Name);
    }

    [Fact]
    public void AMissingAssetYieldsNothing()
    {
        // Installing the wrong flavor would leave a build that cannot start, so no match is a stop.
        var release = Release("SyncOTP-0.2.0-win-x64-framework-dependent.zip");

        Assert.Null(ReleaseAssetPicker.Pick(release, selfContained: true));
    }

    [Fact]
    public void NonZipAssetsAreIgnored()
    {
        var release = Release([.. BothFlavors, "SHA256SUMS.txt", "release-notes.md"]);

        Assert.Equal("SyncOTP-0.2.0-win-x64-selfcontained.zip",
            ReleaseAssetPicker.Pick(release, selfContained: true)?.Name);
    }

    [Fact]
    public void TheTwoFlavorSuffixesDoNotCrossMatch()
    {
        // "framework-dependent" must never satisfy a self-contained install, and vice versa.
        var selfContainedOnly = Release("SyncOTP-0.2.0-win-x64-selfcontained.zip");

        Assert.Null(ReleaseAssetPicker.Pick(selfContainedOnly, selfContained: false));
    }

    [Fact]
    public void MatchingIgnoresCase()
    {
        var release = Release("SyncOTP-0.2.0-WIN-X64-SelfContained.ZIP");

        Assert.NotNull(ReleaseAssetPicker.Pick(release, selfContained: true));
    }

    [Fact]
    public void TwoMatchingAssetsAreTreatedAsAmbiguous()
    {
        var release = Release(
            "SyncOTP-0.2.0-win-x64-selfcontained.zip",
            "SyncOTP-0.2.1-win-x64-selfcontained.zip");

        Assert.Null(ReleaseAssetPicker.Pick(release, selfContained: true));
    }

    [Fact]
    public void AReleaseWithNoAssetsYieldsNothing()
    {
        Assert.Null(ReleaseAssetPicker.Pick(Release(), selfContained: true));
    }
}
