using Soundstage.Core.Apo;
using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Effects;

public class EffectCompilerTests
{
    private static readonly DeviceCapabilities Stereo48 = new(2, 48000);
    private static readonly DeviceCapabilities Surround48 = new(6, 48000);

    // ---- Night mode ----

    [Fact]
    public void NightMode_Disabled_EmitsNothing()
    {
        var result = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: false, Intensity: 80));
        Assert.Empty(result.Commands);
        Assert.Empty(result.PostCommands);
    }

    [Fact]
    public void NightMode_IntensityDrivesCutDepthAndCorner()
    {
        var half = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 50));
        var shelf = Assert.Single(half.Commands.OfType<FilterCommand>());
        Assert.Equal(FilterType.LowShelf, shelf.Type);
        // 50% already delivers a strong cut (~−16 dB) at a mid corner.
        Assert.InRange(-shelf.GainDb, 14.0, 18.0);
        Assert.InRange(shelf.FrequencyHz, 150, 260);

        var full = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 100)).Commands.OfType<FilterCommand>().Single();
        Assert.Equal(-NightModeSettings.MaxBassCutDb, full.GainDb, 2);
        Assert.Equal(NightModeSettings.MaxCornerHz, full.FrequencyHz, 1); // corner opens all the way up

        var low = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 10)).Commands.OfType<FilterCommand>().Single();
        Assert.True(low.FrequencyHz < full.FrequencyHz, "corner rises with intensity");
    }

    [Fact]
    public void NightMode_CornerOverride_PinsTheCorner_IgnoringIntensity()
    {
        var pinned = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 100, BassCornerOverrideHz: 80));
        Assert.Equal(80, pinned.Commands.OfType<FilterCommand>().Single().FrequencyHz);
    }

    [Fact]
    public void NightMode_AppliesLevelReduction_AndNeverHostsAPlugin()
    {
        var result = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 100));
        // Native-only: a bass shelf + extra preamp headroom, and NOTHING external in the path.
        Assert.Equal(NightModeSettings.MaxLevelReductionDb, result.ExtraHeadroomDb, 2);
        Assert.Empty(result.PostCommands);
        Assert.DoesNotContain(result.Commands, c => c is VstPluginCommand);
    }

    [Fact]
    public void NightMode_BassCutOverride_WinsOverIntensity()
    {
        var result = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 10, BassCutDbOverride: -8));
        Assert.Equal(-8, Assert.Single(result.Commands.OfType<FilterCommand>()).GainDb, 2);
    }

    // ---- Loudness ----

    [Fact]
    public void Loudness_MapsIntensityToReferenceLevel_FrontLoaded()
    {
        var result = EffectCompilers.CompileLoudness(new LoudnessSettings(Enabled: true, Intensity: 50));
        var cmd = Assert.Single(result.Commands.OfType<LoudnessCorrectionCommand>());
        Assert.True(cmd.State);
        // 50% is already well past the midpoint of the range (strong, usable).
        Assert.InRange(cmd.ReferenceLevel, -68, -60);

        var max = EffectCompilers.CompileLoudness(new LoudnessSettings(Enabled: true, Intensity: 100));
        Assert.Equal(LoudnessSettings.MaxReferenceLevel, Assert.Single(max.Commands.OfType<LoudnessCorrectionCommand>()).ReferenceLevel, 2);
    }

    [Fact]
    public void Loudness_Disabled_EmitsNothing()
    {
        Assert.Empty(EffectCompilers.CompileLoudness(new LoudnessSettings(Enabled: false)).Commands);
    }

    // ---- Stereo width ----

    [Fact]
    public void Width_At100Percent_IsANoOp()
    {
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 100), Stereo48);
        Assert.Empty(result.Commands);
        Assert.Equal(0, result.BroadbandGainDb, 3);
    }

    [Fact]
    public void Width_140Percent_EmitsExpectedMatrix()
    {
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 140), Stereo48);
        var copy = Assert.Single(result.Commands.OfType<CopyCommand>());
        Assert.Equal("Copy: L=1.2*L-0.2*R R=-0.2*L+1.2*R", copy.Render());
        // Anti-phase gain: 20·log10(1.4) ≈ 2.92 dB.
        Assert.Equal(2.92, result.BroadbandGainDb, 1);
    }

    [Fact]
    public void Width_Narrowing_AddsNoBroadbandGain()
    {
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 60), Stereo48);
        Assert.NotEmpty(result.Commands);
        Assert.Equal(0, result.BroadbandGainDb, 3);
    }

    [Fact]
    public void Width_OnSurroundDevice_AppliesToFrontLR_LeavingOtherChannelsUntouched()
    {
        // Surround-safe: width now works on 5.1/7.1 by only reassigning L and R.
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 160), Surround48);
        var copy = Assert.Single(result.Commands.OfType<CopyCommand>());
        var line = copy.Render();
        Assert.Contains("L=", line);
        Assert.Contains("R=", line);
        // Only L and R are targets — centre/LFE/surrounds are never assigned, so they pass through.
        Assert.DoesNotContain("C=", line);
        Assert.DoesNotContain("SL=", line);
    }

    [Fact]
    public void Width_OnMono_IsSkipped()
    {
        var mono = new DeviceCapabilities(1, 48000);
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 160), mono);
        Assert.Empty(result.Commands);
        Assert.NotNull(result.Note);
    }

    // ---- Ambience ----

    [Fact]
    public void Ambience_EmitsConvolution_WhenIrExists()
    {
        var result = EffectCompilers.CompileAmbience(new AmbienceSettings(Enabled: true, Intensity: 40), Stereo48, _ => @"ir\ambience-48000.wav");
        var conv = Assert.Single(result.Commands.OfType<ConvolutionCommand>());
        Assert.Equal(@"ir\ambience-48000.wav", conv.Path);
    }

    [Fact]
    public void Ambience_SkipsGracefully_WithoutIr()
    {
        var result = EffectCompilers.CompileAmbience(new AmbienceSettings(Enabled: true), Stereo48, _ => null);
        Assert.Empty(result.Commands);
        Assert.NotNull(result.Note);
    }
}
