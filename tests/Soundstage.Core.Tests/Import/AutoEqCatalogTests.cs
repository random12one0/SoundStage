using System.Text;
using Soundstage.Core.Import;
using Xunit;

namespace Soundstage.Core.Tests.Import;

public class AutoEqCatalogTests
{
    private static AutoEqCatalog SyntheticCatalog()
    {
        const string json = """
        {"v":1,"entries":[
          {"n":"Sennheiser HD 650","s":"oratory1990","p":-6.4,"f":[["LSC",105,3.4,0.7],["PK",1274,-2.6,1.62]]},
          {"n":"Sennheiser HD 600","s":"oratory1990","p":-5.0,"f":[["PK",1000,1.0,1.0]]},
          {"n":"Sennheiser HD 650","s":"crinacle","p":-6.0,"f":[["PK",900,2.0,1.0]]},
          {"n":"HIFIMAN HE400se","s":"oratory1990","p":-4.2,"f":[["PK",2000,-2.0,2.0]]},
          {"n":"Apple AirPods Pro 2","s":"rtings","p":-3.1,"f":[["PK",250,1.5,0.9]]}
        ]}
        """;
        return AutoEqCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void EmbeddedCatalog_IsBundled_AndLarge()
    {
        var catalog = AutoEqCatalog.LoadEmbedded();
        Assert.True(catalog.IsAvailable, "embedded AutoEq catalog missing");
        Assert.True(catalog.Count > 5000, $"expected thousands of models, found {catalog.Count}");
    }

    [Fact]
    public void EmbeddedCatalog_EntriesConvertToValidPresets()
    {
        var catalog = AutoEqCatalog.LoadEmbedded();
        var entry = catalog.Search("HD 650").FirstOrDefault();
        Assert.NotNull(entry);

        var preset = entry.ToPreset();
        Assert.NotEmpty(preset.Bands);
        Assert.True(preset.PreampDb <= 0);
        Assert.Equal("Headphones", preset.Category);
    }

    [Fact]
    public void Search_RequiresAllTokens()
    {
        var catalog = SyntheticCatalog();
        Assert.Equal(3, catalog.Search("sennheiser").Count);
        Assert.Equal(2, catalog.Search("sennheiser 650").Count);
        Assert.Empty(catalog.Search("sennheiser 400"));
    }

    [Fact]
    public void Search_RanksExactAndPrefixFirst()
    {
        var catalog = SyntheticCatalog();
        var results = catalog.Search("sennheiser hd 650");
        Assert.Equal("Sennheiser HD 650", results[0].Model);

        var byPrefix = catalog.Search("sennheiser hd 6");
        Assert.All(byPrefix, e => Assert.StartsWith("Sennheiser HD 6", e.Model));
    }

    [Fact]
    public void Search_MatchesSourceToo()
    {
        var catalog = SyntheticCatalog();
        var results = catalog.Search("650 crinacle");
        var entry = Assert.Single(results);
        Assert.Equal("crinacle", entry.Source);
    }

    [Fact]
    public void Search_IsCaseInsensitive_AndTrimsNoise()
    {
        var catalog = SyntheticCatalog();
        Assert.NotEmpty(catalog.Search("  AIRPODS   pro "));
    }

    [Fact]
    public void EmptyQuery_ReturnsNothing()
    {
        Assert.Empty(SyntheticCatalog().Search("   "));
    }

    [Fact]
    public void ToPreset_MapsFilterCodes()
    {
        var entry = SyntheticCatalog().Search("hd 650 oratory1990")[0];
        var preset = entry.ToPreset();
        Assert.Equal(Core.Apo.FilterType.LowShelf, preset.Bands[0].Type);
        Assert.Equal(105, preset.Bands[0].FrequencyHz);
        Assert.Equal("sennheiser-hd-650-oratory1990", preset.Id);
    }
}
