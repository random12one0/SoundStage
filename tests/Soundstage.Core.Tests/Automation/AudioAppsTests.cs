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
        Assert.Equal("Opera GX", AudioApps.Pretty("operagx"));
        Assert.Equal("MPC-HC", AudioApps.Pretty("mpc-hc64"));
        // Unknown falls back to Title Case.
        Assert.Equal("Somegame", AudioApps.Pretty("somegame"));
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("msedge", true)]
    [InlineData("comet", true)]
    [InlineData("spotify", false)]
    [InlineData("vlc", false)]
    public void IsBrowser_RecognizesBrowsersOnly(string process, bool expected)
    {
        Assert.Equal(expected, AudioApps.IsBrowser(process));
    }

    [Theory]
    [InlineData("Never Gonna Give You Up - YouTube - Google Chrome", "YouTube")]
    [InlineData("Stranger Things | Netflix - Google Chrome", "Netflix")]
    [InlineData("shroud - Twitch — Mozilla Firefox", "Twitch")]
    [InlineData("YouTube Music - Chrome", "YouTube Music")] // more specific token wins
    [InlineData("Prime Video - Watch now", "Prime Video")]
    public void DetectStreamingService_ReadsTheTabTitle(string title, string expected)
    {
        Assert.Equal(expected, AudioApps.DetectStreamingService(title));
    }

    [Theory]
    [InlineData("New Tab - Google Chrome")]
    [InlineData("Inbox (3) - user@example.com - Gmail")]
    [InlineData("")]
    [InlineData(null)]
    public void DetectStreamingService_ReturnsNull_WhenNoServiceInTitle(string? title)
    {
        Assert.Null(AudioApps.DetectStreamingService(title));
    }
}
