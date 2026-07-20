using Soundstage.Core.Automation;
using Xunit;

namespace Soundstage.Core.Tests.Automation;

public class AudioAppsTests
{
    [Theory]
    [InlineData("comet")]
    [InlineData("arc")]
    [InlineData("zen")]
    [InlineData("vivaldi")]
    [InlineData("chrome")]
    public void NewerBrowsers_AreRecognized(string process)
    {
        Assert.Contains(process, AudioApps.Browsers);
    }

    [Theory]
    [InlineData("signalrgb")]
    [InlineData("iCUE")]
    [InlineData("nvcontainer")]
    [InlineData("audiodg")]
    public void BackgroundUtilities_AreTreatedAsNoise(string process)
    {
        Assert.True(AudioApps.IsNoise(process));
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("chrome")]
    [InlineData("vlc")]
    public void RealApps_AreNotNoise(string process)
    {
        Assert.False(AudioApps.IsNoise(process));
    }

    [Fact]
    public void Pretty_UsesFriendlyNames()
    {
        Assert.Equal("Spotify", AudioApps.Pretty("spotify"));
        Assert.Equal("Comet", AudioApps.Pretty("comet"));
        Assert.Equal("Edge", AudioApps.Pretty("msedge"));
        Assert.Equal("VLC", AudioApps.Pretty("vlc"));
        // Unknown falls back to Title Case.
        Assert.Equal("Somegame", AudioApps.Pretty("somegame"));
    }
}
