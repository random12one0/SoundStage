using Soundstage.Core.Update;
using Xunit;

namespace Soundstage.Core.Tests.Update;

public class GitHubReleaseParserTests
{
    // Trimmed shape of a real GitHub releases/latest response.
    private const string SampleJson = """
    {
      "tag_name": "v0.4.0",
      "name": "Soundstage v0.4.0",
      "draft": false,
      "prerelease": false,
      "body": "## Soundstage v0.4.0\r\nNew stuff.",
      "html_url": "https://github.com/random12one0/unblockere1231234/releases/tag/v0.4.0",
      "assets": [
        {
          "name": "Soundstage-Setup.exe",
          "browser_download_url": "https://github.com/x/y/releases/download/v0.4.0/Soundstage-Setup.exe",
          "size": 70194825,
          "digest": "sha256:f95f0d98e81060f243ebc6789508efc33ec0b4d4bd6d712a5803e51c877a5af8"
        },
        {
          "name": "Soundstage.exe",
          "browser_download_url": "https://github.com/x/y/releases/download/v0.4.0/Soundstage.exe",
          "size": 75226340,
          "digest": "sha256:60162251c1d4b383f7e22995df8911f9b24d9b177ea3742777e7b78323d28759"
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ExtractsVersionNotesAndAssets()
    {
        var info = GitHubReleaseParser.TryParse(SampleJson);

        Assert.NotNull(info);
        Assert.Equal(new Version(0, 4, 0), info.Version);
        Assert.Equal("Soundstage v0.4.0", info.Name);
        Assert.Contains("New stuff", info.Notes);
        Assert.Equal(2, info.Assets.Count);
    }

    [Fact]
    public void Parse_IdentifiesInstallerAndPortable()
    {
        var info = GitHubReleaseParser.TryParse(SampleJson)!;

        Assert.Equal("Soundstage-Setup.exe", info.Installer!.Name);
        Assert.Equal("Soundstage.exe", info.Portable!.Name);
        Assert.Same(info.Installer, info.PreferredAsset); // installer preferred
        Assert.Equal("f95f0d98e81060f243ebc6789508efc33ec0b4d4bd6d712a5803e51c877a5af8", info.Installer.Sha256);
        Assert.Equal(70194825, info.Installer.SizeBytes);
    }

    [Fact]
    public void Parse_SkipsDrafts()
    {
        Assert.Null(GitHubReleaseParser.TryParse("""{"tag_name":"v9.9.9","draft":true,"assets":[]}"""));
    }

    [Fact]
    public void Parse_RejectsUnparseableTag()
    {
        Assert.Null(GitHubReleaseParser.TryParse("""{"tag_name":"nightly","assets":[]}"""));
    }

    [Fact]
    public void Parse_ToleratesGarbage()
    {
        Assert.Null(GitHubReleaseParser.TryParse("not json"));
        Assert.Null(GitHubReleaseParser.TryParse("[]"));
        Assert.Null(GitHubReleaseParser.TryParse("{}"));
    }

    [Fact]
    public void Parse_HandlesReleaseWithNoAssets()
    {
        var info = GitHubReleaseParser.TryParse("""{"tag_name":"v1.0.0","assets":[]}""");
        Assert.NotNull(info);
        Assert.Empty(info.Assets);
        Assert.Null(info.PreferredAsset);
    }
}
