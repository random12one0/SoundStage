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
    public void NightMode_IntensityDrivesShelfDepth()
    {
        var half = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 50));
        var shelf = Assert.Single(half.Commands.OfType<FilterCommand>());
        Assert.Equal(FilterType.LowShelf, shelf.Type);
        Assert.Equal(90, shelf.FrequencyHz);
        Assert.Equal(-4.5, shelf.GainDb, 2);

        var full = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 100));
        Assert.Equal(-9.0, Assert.Single(full.Commands.OfType<FilterCommand>()).GainDb, 2);
    }

    [Fact]
    public void NightMode_WithoutVst_DegradesHonestly()
    {
        var result = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 100));
        Assert.NotNull(result.Note);
        Assert.Equal(4.0, result.ExtraHeadroomDb, 2);
        Assert.Empty(result.PostCommands);
    }

    [Fact]
    public void NightMode_WithVst_EmitsCompressorAsPostCommand()
    {
        var settings = new NightModeSettings(
            Enabled: true,
            Intensity: 60,
            UseVstCompressor: true,
            VstLibraryPath: @"C:\VST\LoudMax64.dll");

        var result = EffectCompilers.CompileNightMode(settings);

        Assert.Null(result.Note);
        Assert.Equal(0, result.ExtraHeadroomDb);
        var vst = Assert.Single(result.PostCommands.OfType<VstPluginCommand>());
        Assert.Equal(@"C:\VST\LoudMax64.dll", vst.LibraryPath);
    }

    [Fact]
    public void NightMode_BassCutOverride_WinsOverIntensity()
    {
        var result = EffectCompilers.CompileNightMode(new NightModeSettings(Enabled: true, Intensity: 10, BassCutDbOverride: -8));
        Assert.Equal(-8, Assert.Single(result.Commands.OfType<FilterCommand>()).GainDb, 2);
    }

    // ---- Loudness ----

    [Fact]
    public void Loudness_MapsIntensityToReferenceLevel()
    {
        var result = EffectCompilers.CompileLoudness(new LoudnessSettings(Enabled: true, Intensity: 50));
        var cmd = Assert.Single(result.Commands.OfType<LoudnessCorrectionCommand>());
        Assert.True(cmd.State);
        Assert.Equal(-22, cmd.ReferenceLevel, 2);

        var max = EffectCompilers.CompileLoudness(new LoudnessSettings(Enabled: true, Intensity: 100));
        Assert.Equal(-30, Assert.Single(max.Commands.OfType<LoudnessCorrectionCommand>()).ReferenceLevel, 2);
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
    public void Width_OnSurroundDevice_IsHardBlocked()
    {
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 160), Surround48);
        Assert.Empty(result.Commands);
        Assert.NotNull(result.Note);
        Assert.Contains("stereo-only", result.Note);
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
