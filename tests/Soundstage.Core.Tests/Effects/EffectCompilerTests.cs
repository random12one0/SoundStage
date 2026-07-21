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
    public void Width_At0Percent_IsANoOp()
    {
        // 0% is untouched now (the slider is one-directional: 0 → widest).
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 0), Stereo48);
        Assert.Empty(result.Commands);
        Assert.Equal(0, result.BroadbandGainDb, 3);
    }

    [Fact]
    public void Width_AtMax_EmitsSingleLineMatrix_NoScratchChannels()
    {
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 100), Stereo48);
        var copy = Assert.Single(result.Commands.OfType<CopyCommand>());
        // ONE parallel-evaluated line, names only L/R, invents no virtual channels. 100% = the
        // capped max width factor (1.5) → a=(1+1.5)/2=1.25, b=(1−1.5)/2=−0.25.
        Assert.Equal("Copy: L=1.25*L-0.25*R R=1.25*R-0.25*L", copy.Render());
        Assert.DoesNotContain("SS_", copy.Render());
        // Anti-phase gain: 20·log10(1.5) ≈ 3.52 dB.
        Assert.Equal(3.52, result.BroadbandGainDb, 1);
    }

    [Fact]
    public void Width_ScalesBetweenNeutralAndMax()
    {
        // Every amount widens (w > 1) and never exceeds the mono-safe cap.
        var mid = new StereoWidthSettings(Enabled: true, WidthPercent: 50).Width;
        Assert.InRange(mid, 1.0, StereoWidthSettings.MaxWidthFactor);
        Assert.Equal(StereoWidthSettings.MaxWidthFactor, new StereoWidthSettings(Enabled: true, WidthPercent: 100).Width, 3);
        Assert.Equal(1.0, new StereoWidthSettings(Enabled: true, WidthPercent: 0).Width, 3);
    }

    [Fact]
    public void Width_OnSurroundDevice_AppliesToFrontLR_LeavingOtherChannelsUntouched()
    {
        // Surround-safe: width works on 5.1/7.1 by only rebuilding L and R.
        var result = EffectCompilers.CompileStereoWidth(new StereoWidthSettings(Enabled: true, WidthPercent: 160), Surround48);
        var line = string.Join("\n", result.Commands.OfType<CopyCommand>().Select(c => c.Render()));
        Assert.Contains("L=", line);
        Assert.Contains("R=", line);
        // Only L and R are assignment targets — centre/LFE/surrounds are never written, so they pass through.
        Assert.DoesNotContain("C=", line);
        Assert.DoesNotContain("LFE=", line);
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

    // ---- Fidelity ----

    [Fact]
    public void Fidelity_Disabled_EmitsNothing()
    {
        Assert.Empty(EffectCompilers.CompileFidelity(new FidelitySettings(Enabled: false, Intensity: 100)).Commands);
    }

    [Fact]
    public void Fidelity_EmitsPresencePeakAndAirShelf_ScaledByIntensity()
    {
        var full = EffectCompilers.CompileFidelity(new FidelitySettings(Enabled: true, Intensity: 100));
        var filters = full.Commands.OfType<FilterCommand>().ToList();
        Assert.Equal(2, filters.Count);

        var presence = filters.Single(f => f.Type == FilterType.Peaking);
        Assert.Equal(FidelitySettings.PresenceHz, presence.FrequencyHz, 1);
        Assert.Equal(FidelitySettings.MaxPresenceDb, presence.GainDb, 2);

        var air = filters.Single(f => f.Type == FilterType.HighShelf);
        Assert.Equal(FidelitySettings.AirHz, air.FrequencyHz, 1);
        Assert.Equal(FidelitySettings.MaxAirDb, air.GainDb, 2);

        // Front-loaded: 50% is already most of the way there, and less than full.
        var half = EffectCompilers.CompileFidelity(new FidelitySettings(Enabled: true, Intensity: 50))
            .Commands.OfType<FilterCommand>().Single(f => f.Type == FilterType.HighShelf);
        Assert.InRange(half.GainDb, FidelitySettings.MaxAirDb * 0.5, FidelitySettings.MaxAirDb);
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

    // ---- VST rack ----

    [Fact]
    public void VstRack_EmitsChannelRoutedPluginLines_AndResetsScope()
    {
        var bass = new VstRackEffect("bass", "Virtual Bass", "sub", "BassKit.dll", VstRackEffect.RouteSub,
            [new VstIntensityParam("Drive", 0.0, 1.0)]);
        var warmth = new VstRackEffect("warmth", "Warmth", "tape", "ToTape.dll", VstRackEffect.RouteAll, []);

        var cmds = EffectCompilers.CompileVstRack([(bass, 100), (warmth, 50)], 6, name => $@"C:\plugins\{name}");
        var text = string.Join("\r\n", cmds.Select(c => c.Render()));

        // Bass routed to the sub, with its intensity mapped onto the Drive parameter (100% → 1).
        Assert.Contains("Channel: LFE", text);
        Assert.Contains("VSTPlugin: Library \"C:\\plugins\\BassKit.dll\" \"Drive\" 1", text);
        // Warmth across the whole layout, no params.
        Assert.Contains("VSTPlugin: Library \"C:\\plugins\\ToTape.dll\"", text);
        // Channel scope handed back to the full 5.1 layout at the end.
        Assert.EndsWith("Channel: L R C LFE RL RR", text);
    }

    [Fact]
    public void VstRack_SkipsEffectsWhosePluginIsNotInstalled()
    {
        var bass = new VstRackEffect("bass", "Virtual Bass", "sub", "BassKit.dll", VstRackEffect.RouteSub, []);
        var cmds = EffectCompilers.CompileVstRack([(bass, 100)], 6, _ => null); // nothing installed yet
        Assert.Empty(cmds);
    }
}
