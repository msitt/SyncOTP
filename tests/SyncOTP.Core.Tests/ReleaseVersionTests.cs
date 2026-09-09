using SyncOTP.Core;
using Xunit;

namespace SyncOTP.Core.Tests;

public class ReleaseVersionTests
{
    private static ReleaseVersion Parse(string text)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version), $"{text} should parse");
        return version;
    }

    [Fact]
    public void APlainVersionParses()
    {
        var version = Parse("0.1.1");

        Assert.Equal(0, version.Major);
        Assert.Equal(1, version.Minor);
        Assert.Equal(1, version.Patch);
        Assert.False(version.IsPreRelease);
    }

    [Fact]
    public void ALeadingVIsIgnored()
    {
        // Release tags are v0.1.1 while AppVersion.Display is 0.1.1, and the two must compare equal.
        Assert.Equal(Parse("0.2.0"), Parse("v0.2.0"));
        Assert.Equal(Parse("0.2.0"), Parse("V0.2.0"));
    }

    [Fact]
    public void BuildMetadataAfterAPlusIsIgnored()
    {
        Assert.Equal(Parse("0.2.0"), Parse("0.2.0+8f3a1c9"));
    }

    [Fact]
    public void AMissingPatchComponentIsZero()
    {
        Assert.Equal(Parse("1.2.0"), Parse("1.2"));
        Assert.Equal(Parse("1.0.0"), Parse("1"));
    }

    [Fact]
    public void AFourthComponentIsAcceptedAndIgnored()
    {
        // AppVersion falls back to Version.ToString(), which is four parts, when the assembly has
        // no informational version.
        Assert.Equal(Parse("0.1.1"), Parse("0.1.1.0"));
    }

    [Theory]
    [InlineData("0.1.2", "0.1.1")]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("1.0.0", "0.9.9")]
    public void AHigherVersionIsNewer(string candidate, string current)
    {
        Assert.True(ReleaseVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void AnEqualVersionIsNotNewer()
    {
        Assert.False(ReleaseVersion.IsNewer("v0.1.1", "0.1.1"));
    }

    [Fact]
    public void AnOlderVersionIsNotNewer()
    {
        // A locally built copy that is ahead of the published release must never be downgraded.
        Assert.False(ReleaseVersion.IsNewer("0.1.1", "0.2.0"));
    }

    [Fact]
    public void APreReleaseIsOlderThanItsRelease()
    {
        Assert.True(Parse("0.2.0-beta.1").CompareTo(Parse("0.2.0")) < 0);
        Assert.True(ReleaseVersion.IsNewer("0.2.0", "0.2.0-beta.1"));
        Assert.False(ReleaseVersion.IsNewer("0.2.0-beta.1", "0.2.0"));
    }

    [Fact]
    public void APreReleaseStillBeatsAnOlderRelease()
    {
        Assert.True(ReleaseVersion.IsNewer("0.2.0-beta.1", "0.1.1"));
    }

    [Fact]
    public void PreReleaseIdentifiersCompareNumericallyThenAlphabetically()
    {
        // Numeric identifiers compare as numbers, so 10 outranks 2, and rank below alphanumeric ones.
        Assert.True(Parse("0.2.0-alpha.1").CompareTo(Parse("0.2.0-alpha.2")) < 0);
        Assert.True(Parse("0.2.0-alpha.2").CompareTo(Parse("0.2.0-alpha.10")) < 0);
        Assert.True(Parse("0.2.0-alpha.10").CompareTo(Parse("0.2.0-beta")) < 0);
    }

    [Fact]
    public void ALongerPreReleaseOutranksThePrefixItExtends()
    {
        Assert.True(Parse("0.2.0-rc").CompareTo(Parse("0.2.0-rc.1")) < 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.-2.3")]
    [InlineData("0.2.0-")]
    public void AnUnparseableVersionIsNeverNewer(string? text)
    {
        // Failing closed matters: offering an update we cannot reason about is worse than missing one.
        Assert.False(ReleaseVersion.TryParse(text, out _));
        Assert.False(ReleaseVersion.IsNewer(text, "0.1.1"));
        Assert.False(ReleaseVersion.IsNewer("0.9.9", text));
    }

    [Fact]
    public void TheRunningAppVersionParses()
    {
        // The regression test for the day someone gives Directory.Build.props a shape the update
        // check cannot read, which would silently disable updating altogether.
        Assert.True(ReleaseVersion.TryParse(AppVersion.Display, out _));
    }

    [Fact]
    public void ToStringRoundTrips()
    {
        Assert.Equal("0.1.1", Parse("v0.1.1+abc").ToString());
        Assert.Equal("0.2.0-beta.1", Parse("v0.2.0-beta.1").ToString());
    }
}
