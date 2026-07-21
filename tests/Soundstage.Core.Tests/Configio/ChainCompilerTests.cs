using Soundstage.Core.Apo;
using Soundstage.Core.Configio;
using Soundstage.Core.Effects;
using Soundstage.Core.Presets;
using Soundstage.Core.State;
using Xunit;

namespace Soundstage.Core.Tests.Configio;

public class ChainCompilerTests
{
    private static EqPreset MusicPreset => new()
    {
        Id = "music",
        Name = "Music",
        PreampDb = 0,
        Bands =
        [
            new EqBand(FilterType.LowShelf, 80, 2.0, 0.707),
            new EqBand(FilterType.Peaking, 250, -1.5, 1.0),
            new EqBand(FilterType.HighShelf, 8000, 1.5, 0.707),
        ],
    };

    private static SoundstageState StateWith(params DeviceProfile[] profiles)
    {
        var state = new SoundstageState();
        state.Profiles.AddRange(profiles);
        return state;
    }

    private static DeviceProfile SpeakerProfile(string? presetId = "music") => new()
    {
        EndpointId = "{0.0.0.00000000}.{aaaa1111-2222-3333-4444-555566667777}",
        FriendlyName = "Onkyo TX-NR676",
        ActivePresetId = presetId,
        LastKnownChannels = 6,
        LastKnownSampleRateHz = 48000,
    };

    private static DeviceProfile HeadphoneProfile() => new()
    {
        EndpointId = "{0.0.0.00000000}.{bbbb1111-2222-3333-4444-555566667777}",
        FriendlyName = "Headphones",
        ActivePresetId = null,
        LastKnownChannels = 2,
    };

    [Fact]
    public void EmptyState_CompilesHeaderOnly()
    {
        var compilation = ChainCompiler.Compile(new SoundstageState(), _ => null);
        Assert.Contains("Managed by Soundstage", compilation.RenderedText);
        Assert.Contains("No devices configured", compilation.RenderedText);
        Assert.Empty(compilation.Devices);
    }

    [Fact]
    public void SingleProfile_AppliesGlobally_WithoutDeviceScoping()
    {
        // One device = nothing to disambiguate. Skipping Device: sidesteps APO's silent
        // no-match failure mode entirely for the common single-output setup.
        var compilation = ChainCompiler.Compile(StateWith(SpeakerProfile()), _ => MusicPreset);
        Assert.DoesNotContain("Device:", compilation.RenderedText);
        Assert.Contains("Filter 1:", compilation.RenderedText);
    }

    [Fact]
    public void TwoProfiles_UseEndpointGuids_AsMatchSpecs()
    {
        var compilation = ChainCompiler.Compile(StateWith(SpeakerProfile(), HeadphoneProfile()), _ => MusicPreset);
        Assert.Contains("Device: {aaaa1111-2222-3333-4444-555566667777}", compilation.RenderedText);
        Assert.Contains("Device: {bbbb1111-2222-3333-4444-555566667777}", compilation.RenderedText);
    }

    [Fact]
    public void PresetBoost_GetsAutoTrimmedPreamp()
    {
        var compilation = ChainCompiler.Compile(StateWith(SpeakerProfile()), _ => MusicPreset);

        var report = Assert.Single(compilation.Devices);
        Assert.True(report.Headroom.PeakBoostDb > 1.5);
        Assert.True(report.Headroom.RecommendedPreampDb < -1.5);

        // The rendered preamp equals the recommendation.
        Assert.Contains($"Preamp: {report.Headroom.RecommendedPreampDb:0.0#} dB".Replace(',', '.'), compilation.RenderedText);
    }

    [Fact]
    public void ClippingProtectionOff_MeasuresPeakButSkipsAutoTrim()
    {
        var state = StateWith(SpeakerProfile());
        state.Settings.ClippingProtection = false;

        var compilation = ChainCompiler.Compile(state, _ => MusicPreset);
        var report = Assert.Single(compilation.Devices);

        // The boost is still measured…
        Assert.True(report.Headroom.PeakBoostDb > 1.5);
        // …but no protective trim is applied, so the author's (0 dB) preamp stands and no line is emitted.
        Assert.Equal(0, report.Headroom.AutoTrimDb, 3);
        Assert.DoesNotContain("Preamp:", compilation.RenderedText);
    }

    [Fact]
    public void WidthOnSurroundProfile_EmitsFrontLRCopy_SurroundSafe()
    {
        var profile = SpeakerProfile(); // 5.1
        profile.Effects = EffectSettings.Default with
        {
            StereoWidth = new StereoWidthSettings(Enabled: true, WidthPercent: 150),
        };

        var compilation = ChainCompiler.Compile(StateWith(profile), _ => MusicPreset);

        // Now applies on surround too — but only touches L and R.
        Assert.Contains("Copy: L=", compilation.RenderedText);
        Assert.DoesNotContain("C=", compilation.RenderedText);
    }

    [Fact]
    public void WidthOnStereoProfile_EmitsCopy()
    {
        var profile = HeadphoneProfile();
        profile.Effects = EffectSettings.Default with
        {
            StereoWidth = new StereoWidthSettings(Enabled: true, WidthPercent: 100),
        };

        var compilation = ChainCompiler.Compile(StateWith(profile), _ => null);
        Assert.Contains("Copy: L=1.25*L-0.25*R R=1.25*R-0.25*L", compilation.RenderedText);
        Assert.DoesNotContain("SS_", compilation.RenderedText);
    }

    [Fact]
    public void NightShelf_ComesBeforeLoudness_AndNeverEmitsAPlugin()
    {
        var profile = SpeakerProfile();
        profile.Effects = EffectSettings.Default with
        {
            NightMode = new NightModeSettings(Enabled: true, Intensity: 70),
            Loudness = new LoudnessSettings(Enabled: true),
        };

        var text = ChainCompiler.Compile(StateWith(profile), _ => MusicPreset).RenderedText;

        var loudnessIndex = text.IndexOf("LoudnessCorrection:", StringComparison.Ordinal);
        var shelfIndex = text.IndexOf("Night mode: bass shelf", StringComparison.Ordinal);
        Assert.True(shelfIndex > 0);
        Assert.True(loudnessIndex > shelfIndex);
        // Night mode is 100% native — it must never host a plugin (that was the silence bug).
        Assert.DoesNotContain("VSTPlugin:", text);
    }

    [Fact]
    public void TwoProfiles_GetIndependentSections_AndFilterNumberingRestarts()
    {
        var speakers = SpeakerProfile();
        var phones = HeadphoneProfile();
        phones.ActivePresetId = "music";

        var text = ChainCompiler.Compile(StateWith(speakers, phones), _ => MusicPreset).RenderedText;

        Assert.Contains("{aaaa1111-2222-3333-4444-555566667777}", text);
        Assert.Contains("{bbbb1111-2222-3333-4444-555566667777}", text);
        // Filter numbering restarts per device.
        Assert.Equal(2, text.Split("Filter 1: ").Length - 1);
    }

    [Fact]
    public void DisabledProfile_GetsNoSection()
    {
        var profile = SpeakerProfile();
        profile.Enabled = false;
        var text = ChainCompiler.Compile(StateWith(profile), _ => MusicPreset).RenderedText;
        Assert.DoesNotContain("Device:", text);
    }

    [Fact]
    public void MissingPreset_CompilesWithoutEq_AndNotes()
    {
        var profile = SpeakerProfile("ghost");
        var compilation = ChainCompiler.Compile(StateWith(profile), _ => null);
        Assert.DoesNotContain("Filter", compilation.RenderedText);
        Assert.Contains(Assert.Single(compilation.Devices).Notes, n => n.Contains("ghost"));
    }

    [Fact]
    public void GoldenEndToEnd_FullState_MatchesExactly()
    {
        var speakers = SpeakerProfile();
        // Explicit overrides keep this a byte-exact STRUCTURE golden; the intensity→amount
        // curve is covered numerically in IntensityCurveTests / EffectCompilerTests.
        speakers.Effects = EffectSettings.Default with
        {
            NightMode = new NightModeSettings(Enabled: true, Intensity: 50, BassCornerOverrideHz: 90, BassCutDbOverride: -4.5),
            Loudness = new LoudnessSettings(Enabled: true, ReferenceLevelOverride: -22),
        };

        var flat = EqPreset.CreateFlat();
        var compilation = ChainCompiler.Compile(
            StateWith(speakers),
            id => id == "music" ? MusicPreset : flat);

        const string expected =
            "# Managed by Soundstage. Generated processing chain — do not edit (use user.txt).\r\n" +
            "\r\n" +
            "# ── Onkyo TX-NR676 (5.1) — preset: Music\r\n" +
            // Degraded night-mode headroom (6·curve(50) ≈ 4.1) dominates the clip ceiling here.
            "Preamp: -4.1 dB\r\n" +
            "Filter 1: ON LSC Fc 80 Hz Gain 2.0 dB Q 0.707\r\n" +
            "Filter 2: ON PK Fc 250 Hz Gain -1.5 dB Q 1\r\n" +
            "Filter 3: ON HSC Fc 8000 Hz Gain 1.5 dB Q 0.707\r\n" +
            "# Night mode: bass shelf\r\n" +
            "Filter 4: ON LSC Fc 90 Hz Gain -4.5 dB Q 0.707\r\n" +
            "# Loudness compensation\r\n" +
            "LoudnessCorrection: State 1 ReferenceLevel -22 Attenuation 0\r\n";

        Assert.Equal(expected, compilation.RenderedText);
    }
}
