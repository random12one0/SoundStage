using Soundstage.Core.Update;
using Xunit;

namespace Soundstage.Core.Tests.Update;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData("v0.4.0", 0, 4, 0)]
    [InlineData("0.4.0", 0, 4, 0)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("v2.0", 2, 0, 0)]
    [InlineData("v1", 1, 0, 0)]
    [InlineData("v0.4.0-beta.1", 0, 4, 0)]
    public void TryParseTag_ParsesCommonForms(string tag, int major, int minor, int build)
    {
        Assert.True(UpdatePolicy.TryParseTag(tag, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(build, v.Build < 0 ? 0 : v.Build);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void TryParseTag_RejectsGarbage(string? tag)
    {
        Assert.False(UpdatePolicy.TryParseTag(tag, out _));
    }

    [Fact]
    public void IsNewer_DetectsUpgrades()
    {
        Assert.True(UpdatePolicy.IsNewer(new Version(0, 3, 0), new Version(0, 4, 0)));
        Assert.True(UpdatePolicy.IsNewer(new Version(0, 3, 0), new Version(1, 0, 0)));
        Assert.True(UpdatePolicy.IsNewer(new Version(0, 3, 0), new Version(0, 3, 1)));
    }

    [Fact]
    public void IsNewer_IgnoresSameOrOlder_AndFourthComponent()
    {
        Assert.False(UpdatePolicy.IsNewer(new Version(0, 4, 0), new Version(0, 4, 0)));
        Assert.False(UpdatePolicy.IsNewer(new Version(0, 4, 0), new Version(0, 3, 9)));
        // Assembly versions are 4-part (0.4.0.0); the revision must not count.
        Assert.False(UpdatePolicy.IsNewer(new Version(0, 4, 0, 0), new Version(0, 4, 0, 5)));
    }

    [Fact]
    public void FormatSize_IsFriendly()
    {
        Assert.Equal("70.2 MB", UpdatePolicy.FormatSize(73_651_814));
        Assert.Equal("", UpdatePolicy.FormatSize(0));
    }
}
