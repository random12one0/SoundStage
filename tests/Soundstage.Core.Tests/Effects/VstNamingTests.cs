using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Effects;

public class VstNamingTests
{
    [Theory]
    [InlineData("BassKit64.dll", "basskit")]
    [InlineData("BassKit.dll", "basskit")]
    [InlineData("basskit64.DLL", "basskit")]
    [InlineData("Bass Kit (1).dll", "basskit")]        // spaces + copy suffix stripped
    [InlineData("PurestDrive64.dll", "purestdrive")]
    [InlineData("ADClip764.dll", "adclip7")]           // trailing 64 goes, the model number 7 stays
    [InlineData("Air264.dll", "air2")]
    [InlineData("Pressure464.dll", "pressure4")]
    public void NormalizeKey_CanonicalizesNames(string fileName, string expected)
    {
        Assert.Equal(expected, VstNaming.NormalizeKey(fileName));
    }

    [Fact]
    public void NormalizeKey_KeepsDistinctModelsApart()
    {
        // The "64" strip must not collapse Pressure4/Pressure5 or Air2/Air4 into each other.
        Assert.NotEqual(VstNaming.NormalizeKey("Pressure464.dll"), VstNaming.NormalizeKey("Pressure564.dll"));
        Assert.NotEqual(VstNaming.NormalizeKey("Air264.dll"), VstNaming.NormalizeKey("Air464.dll"));
    }

    [Fact]
    public void NormalizeKey_EmptyOrExtensionless_IsSafe()
    {
        Assert.Equal(string.Empty, VstNaming.NormalizeKey(""));
        Assert.Equal("loudmax", VstNaming.NormalizeKey("LoudMax"));
    }

    [Theory]
    [InlineData("BassKit.dll", "BassKit64.dll")]        // catalog bare name vs shipped 64 build
    [InlineData("Air2.dll", "air264.dll")]
    [InlineData("Pressure4.dll", "Pressure4 (1).dll")]
    public void Matches_AcceptsEquivalentForms(string wanted, string candidate)
    {
        Assert.True(VstNaming.Matches(wanted, candidate));
    }

    [Theory]
    [InlineData("Pressure464.dll", "Pressure564.dll")]
    [InlineData("BassKit64.dll", "PurestDrive64.dll")]
    public void Matches_RejectsDifferentPlugins(string wanted, string candidate)
    {
        Assert.False(VstNaming.Matches(wanted, candidate));
    }

    [Fact]
    public void Matches_EmptyNamesNeverMatch()
    {
        Assert.False(VstNaming.Matches("", ""));
        Assert.False(VstNaming.Matches(".dll", "x.dll"));
    }

    [Fact]
    public void EveryCatalogEffect_HasADistinctKey()
    {
        var keys = VstCatalog.All.Select(e => VstNaming.NormalizeKey(e.DllFileName)).ToList();
        Assert.All(keys, k => Assert.NotEqual(string.Empty, k));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
