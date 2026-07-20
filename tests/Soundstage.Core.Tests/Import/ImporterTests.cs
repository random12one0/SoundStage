using Soundstage.Core.Apo;
using Soundstage.Core.Import;
using Soundstage.Core.Presets;
using Xunit;

namespace Soundstage.Core.Tests.Import;

public class ImporterTests
{
    private const string AutoEqSample =
        "Preamp: -6.4 dB\n" +
        "Filter 1: ON LSC Fc 105 Hz Gain 3.4 dB Q 0.70\n" +
        "Filter 2: ON PK Fc 1274 Hz Gain -2.6 dB Q 1.62\n" +
        "Filter 3: ON PK Fc 6337 Hz Gain 4.2 dB Q 2.71\n" +
        "Filter 4: ON HSC Fc 10000 Hz Gain -4.2 dB Q 0.70\n";

    [Fact]
    public void AutoEq_Import_ProducesHeadphonePreset()
    {
        var outcome = AutoEqImporter.Import(AutoEqSample, "Sennheiser HD 650", "AutoEq (oratory1990)");

        Assert.Empty(outcome.Warnings);
        var preset = outcome.Preset;
        Assert.Equal("sennheiser-hd-650", preset.Id);
        Assert.Equal(PresetCategories.Headphones, preset.Category);
        Assert.Equal(-6.4, preset.PreampDb);
        Assert.Equal(4, preset.Bands.Count);
        Assert.Equal(FilterType.LowShelf, preset.Bands[0].Type);
        Assert.Equal(0.70, preset.Bands[0].Q);
    }

    [Fact]
    public void AutoEq_ImportedPreset_RendersNumericallyEquivalentApo()
    {
        // Round-trip: import → compile back to APO lines → parse → compare numerically.
        var preset = AutoEqImporter.Import(AutoEqSample, "Test").Preset;
        var rendered = new ApoDocument([.. preset.ToApoCommands()]).Render();
        var reparsed = ApoConfigParser.Parse(rendered).Document.Commands.OfType<FilterCommand>().ToList();

        Assert.Equal(preset.Bands.Count, reparsed.Count);
        for (var i = 0; i < preset.Bands.Count; i++)
        {
            Assert.Equal(preset.Bands[i].FrequencyHz, reparsed[i].FrequencyHz, 3);
            Assert.Equal(preset.Bands[i].GainDb, reparsed[i].GainDb, 3);
            Assert.Equal(preset.Bands[i].Q, reparsed[i].Q!.Value, 3);
        }
    }

    [Fact]
    public void AutoEq_EmptyFile_Warns()
    {
        var outcome = AutoEqImporter.Import("# nothing here\n", "Empty");
        Assert.Contains(outcome.Warnings, w => w.Contains("No filters"));
    }

    [Fact]
    public void Peace_ApoStyleExport_Imports()
    {
        var outcome = PeaceImporter.Import(AutoEqSample, "From Peace");
        Assert.Equal(4, outcome.Preset.Bands.Count);
        Assert.Equal(-6.4, outcome.Preset.PreampDb);
        Assert.Equal(PresetCategories.Imported, outcome.Preset.Category);
    }

    [Fact]
    public void Peace_IniStyle_WithCommaDecimals_Imports()
    {
        // Peace runs under the user's locale; EU exports carry comma decimals.
        const string peaceFile =
            "[Peace]\n" +
            "presetname=Bass Boost\n" +
            "Preamp=-4,5\n" +
            "Filter1=ON PK Fc 62,5 Hz Gain 5,5 dB Q 1,41\n" +
            "Filter2=ON LSC Fc 105 Hz Gain 2,0 dB Q 0,70\n" +
            "Filter3=ON None\n";

        var outcome = PeaceImporter.Import(peaceFile, "Bass Boost");

        Assert.Equal(-4.5, outcome.Preset.PreampDb);
        Assert.Equal(2, outcome.Preset.Bands.Count);
        Assert.Equal(62.5, outcome.Preset.Bands[0].FrequencyHz);
        Assert.Equal(5.5, outcome.Preset.Bands[0].GainDb);
        Assert.Equal(1.41, outcome.Preset.Bands[0].Q);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public void Peace_OffFilters_KeepDisabledState()
    {
        var outcome = PeaceImporter.Import("Filter1=OFF PK Fc 100 Hz Gain 3 dB Q 1\n", "X");
        var band = Assert.Single(outcome.Preset.Bands);
        Assert.False(band.Enabled);
    }

    [Fact]
    public void Peace_GarbageLines_AreSkippedQuietly()
    {
        var outcome = PeaceImporter.Import("speakers=yes\nvolume=12\nFilter1=ON PK Fc 100 Hz Gain 1 dB Q 1\n", "X");
        Assert.Single(outcome.Preset.Bands);
        Assert.Empty(outcome.Warnings);
    }
}
