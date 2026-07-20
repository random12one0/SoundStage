using Soundstage.Core.Apo;
using Soundstage.Core.Presets;
using Soundstage.Core.Tests.Support;
using Xunit;

namespace Soundstage.Core.Tests.Presets;

public class PresetStoreTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly TestClock _clock = new();
    private const string Dir = @"C:\AppData\Soundstage\presets";

    private PresetStore CreateStore() => new(_fs, Dir, _clock);

    [Fact]
    public void BuiltIns_LoadFromEmbeddedResources_InCuratedOrder()
    {
        var store = CreateStore();
        var builtIns = store.All.Where(p => p.IsBuiltIn).ToList();

        Assert.Equal(13, builtIns.Count);
        // Curated order leads; flat is always first.
        Assert.Equal("flat", builtIns[0].Id);
        Assert.Equal("music", builtIns[1].Id);
        Assert.Contains(builtIns, p => p.Id == "bass-boost");
        Assert.Contains(builtIns, p => p.Id == "rock");
        Assert.All(builtIns, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void CuratedPresets_AllStayWithinModestAutoTrim()
    {
        // A curated preset must never demand heavy clipping trim — that would defeat
        // "immediately useful out of the box".
        foreach (var preset in EmbeddedPresets.LoadAll())
        {
            var filters = preset.Bands.Select(b => new FilterCommand(b.Type, b.FrequencyHz, b.GainDb, b.Q, b.Enabled));
            var report = Core.Dsp.HeadroomAnalyzer.Analyze(preset.PreampDb, filters);
            Assert.True(report.AutoTrimDb < 4.0, $"{preset.Id} needs {report.AutoTrimDb:0.0} dB trim");
        }
    }

    [Fact]
    public void SaveAndReload_RoundTripsUserPreset()
    {
        var store = CreateStore();
        var preset = new EqPreset
        {
            Id = "my-preset",
            Name = "My Preset",
            Bands = [new EqBand(FilterType.Peaking, 1000, 3, 1.41)],
            PreampDb = -3.5,
        };

        store.Save(preset);

        var reloaded = new PresetStore(_fs, Dir, _clock).Get("my-preset");
        Assert.NotNull(reloaded);
        Assert.Equal("My Preset", reloaded.Name);
        Assert.Equal(-3.5, reloaded.PreampDb);
        var band = Assert.Single(reloaded.Bands);
        Assert.Equal(FilterType.Peaking, band.Type);
        Assert.Equal(1.41, band.Q);
    }

    [Fact]
    public void BuiltIns_CannotBeSavedRenamedOrDeleted()
    {
        var store = CreateStore();
        var flat = store.Get("flat")!;

        Assert.Throws<InvalidOperationException>(() => store.Save(flat));
        Assert.Throws<InvalidOperationException>(() => store.Rename("flat", "Flatter"));
        Assert.Throws<InvalidOperationException>(() => store.Delete("flat"));
    }

    [Fact]
    public void Duplicate_OfBuiltIn_CreatesEditableCustomCopy()
    {
        var store = CreateStore();
        var copy = store.Duplicate("music", "Music Tweaked");

        Assert.False(copy.IsBuiltIn);
        Assert.Equal("music-tweaked", copy.Id);
        Assert.Equal(PresetCategories.Custom, copy.Category);
        Assert.Equal(store.Get("music")!.Bands.Count, copy.Bands.Count);

        // Now editable.
        copy.PreampDb = -1;
        store.Save(copy);
    }

    [Fact]
    public void Duplicate_CollidingNames_GetUniqueIds()
    {
        var store = CreateStore();
        var first = store.Duplicate("music");
        var second = store.Duplicate("music");
        Assert.NotEqual(first.Id, second.Id);
        Assert.StartsWith("music-copy", second.Id);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var store = CreateStore();
        var copy = store.Duplicate("flat", "Temp");
        Assert.True(_fs.FileExists($@"{Dir}\{copy.Id}.json"));

        store.Delete(copy.Id);
        Assert.False(_fs.FileExists($@"{Dir}\{copy.Id}.json"));
        Assert.Null(store.Get(copy.Id));
    }

    [Fact]
    public void CorruptUserPresetFile_IsSkipped_NotDeleted()
    {
        _fs.WriteAllTextAtomic($@"{Dir}\broken.json", "{not json");
        var store = CreateStore();

        Assert.Equal(13, store.All.Count); // just the built-ins
        Assert.True(_fs.FileExists($@"{Dir}\broken.json"));
    }

    [Theory]
    [InlineData("My Great Preset!", "my-great-preset")]
    [InlineData("  Sennheiser HD 650 (oratory1990)  ", "sennheiser-hd-650-oratory1990")]
    [InlineData("###", "preset")]
    public void Slugify_ProducesCleanIds(string name, string expected)
    {
        Assert.Equal(expected, PresetStore.Slugify(name));
    }
}
