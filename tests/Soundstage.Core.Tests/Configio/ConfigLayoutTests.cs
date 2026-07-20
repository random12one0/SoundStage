using Soundstage.Core.Configio;
using Xunit;

namespace Soundstage.Core.Tests.Configio;

public class ConfigLayoutTests
{
    private readonly ConfigLayout _layout = new(@"C:\Program Files\EqualizerAPO\config");

    [Fact]
    public void Paths_AreWindowsStyle_RegardlessOfHostOs()
    {
        Assert.Equal(@"C:\Program Files\EqualizerAPO\config\config.txt", _layout.ConfigTxtPath);
        Assert.Equal(@"C:\Program Files\EqualizerAPO\config\Soundstage", _layout.AppDirectory);
        Assert.Equal(@"C:\Program Files\EqualizerAPO\config\Soundstage\app.txt", _layout.SwitchFilePath);
        Assert.Equal(@"C:\Program Files\EqualizerAPO\config\Soundstage\user.txt", _layout.UserFilePath);
        Assert.Equal(@"C:\Program Files\EqualizerAPO\config\Soundstage\chain.txt", _layout.ChainFilePath);
        Assert.Equal(@"C:\Program Files\EqualizerAPO\config\Soundstage\backups", _layout.BackupsDirectory);
    }

    [Fact]
    public void TrailingSeparator_IsTolerated()
    {
        var layout = new ConfigLayout(@"C:\config\");
        Assert.Equal(@"C:\config\config.txt", layout.ConfigTxtPath);
    }

    [Fact]
    public void Stub_IncludesAppSwitchFile_AndCarriesMarker()
    {
        var stub = _layout.BuildStubContent();
        Assert.Contains(@"Include: Soundstage\app.txt", stub);
        Assert.True(ConfigLayout.ContainsManagedMarker(stub));
    }

    [Fact]
    public void Switch_Active_IncludesChain()
    {
        var content = _layout.BuildSwitchContent(bypassed: false);
        Assert.Contains("Include: user.txt", content);
        Assert.Contains("Include: chain.txt", content);
        Assert.False(ConfigLayout.IsSwitchBypassed(content));
    }

    [Fact]
    public void Switch_Bypassed_DropsChainInclude_KeepsUserInclude()
    {
        var content = _layout.BuildSwitchContent(bypassed: true);
        Assert.Contains("Include: user.txt", content);
        // The chain include must not be an active line — only mentioned in a comment.
        foreach (var line in content.Split("\r\n"))
        {
            if (line.Contains("chain.txt", StringComparison.Ordinal))
            {
                Assert.StartsWith("#", line);
            }
        }

        Assert.True(ConfigLayout.IsSwitchBypassed(content));
    }
}
