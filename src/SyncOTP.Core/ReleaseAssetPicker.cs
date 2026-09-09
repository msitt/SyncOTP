namespace SyncOTP.Core;

/// <summary>
/// Chooses the one asset on a release that matches how this copy of SyncOTP was installed. The
/// two suffixes are fixed by the release workflow, which names every zip
/// <c>SyncOTP-&lt;version&gt;-win-x64-&lt;flavor&gt;.zip</c>.
/// </summary>
public static class ReleaseAssetPicker
{
    public const string SelfContainedSuffix = "-win-x64-selfcontained.zip";
    public const string FrameworkDependentSuffix = "-win-x64-framework-dependent.zip";

    /// <summary>
    /// The asset for this install's flavor, or null when the release has none or somehow has more
    /// than one. Null is a hard stop: offering the wrong flavor would install a build that cannot
    /// start on this machine.
    /// </summary>
    public static ReleaseAsset? Pick(ReleaseInfo release, bool selfContained)
    {
        var suffix = selfContained ? SelfContainedSuffix : FrameworkDependentSuffix;

        ReleaseAsset? found = null;
        foreach (var asset in release.Assets)
        {
            if (!asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) return null;
            found = asset;
        }

        return found;
    }
}
