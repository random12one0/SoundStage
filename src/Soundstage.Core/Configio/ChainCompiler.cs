using Soundstage.Core.Apo;
using Soundstage.Core.Dsp;
using Soundstage.Core.Effects;
using Soundstage.Core.Presets;
using Soundstage.Core.State;

namespace Soundstage.Core.Configio;

/// <summary>Per-device outcome of a chain compilation.</summary>
public sealed record DeviceChainReport(
    string EndpointId,
    string FriendlyName,
    string? PresetName,
    HeadroomReport Headroom,
    IReadOnlyList<string> Notes);

public sealed record ChainCompilation(ApoDocument Document, string RenderedText, IReadOnlyList<DeviceChainReport> Devices);

/// <summary>
/// Pure function: application state → generated chain.txt. One <c>Device:</c> section per
/// enabled profile so each endpoint gets exactly its own processing; the analyzer clamps
/// each section's preamp for clipping safety.
///
/// Section order per device: preamp → preset EQ → night-mode shelf → fidelity → stereo width →
/// ambience → loudness correction.
/// </summary>
public static class ChainCompiler
{
    public static ChainCompilation Compile(
        SoundstageState state,
        Func<string, EqPreset?> presetResolver,
        Func<int, string?>? ambienceIrResolver = null,
        Func<string, string?>? vstPluginResolver = null)
    {
        var document = new ApoDocument();
        var reports = new List<DeviceChainReport>();

        document.Commands.Add(new CommentCommand($"{ConfigLayout.ManagedMarker}. Generated processing chain — do not edit (use user.txt)."));

        var profiles = state.Profiles.Where(p => p.Enabled).ToList();
        if (profiles.Count == 0)
        {
            document.Commands.Add(new CommentCommand("No devices configured."));
        }

        // With a single profile there is nothing to disambiguate — apply globally, with no
        // Device: scoping. This sidesteps the one silent-failure mode of APO's device
        // matching (a non-matching pattern applies to nothing, with no error anywhere).
        // Scoped sections only appear once a second device profile exists.
        var scopePerDevice = profiles.Count > 1;

        foreach (var profile in profiles)
        {
            document.Commands.Add(BlankLineCommand.Instance);
            CompileDevice(document, profile, state, presetResolver, ambienceIrResolver, vstPluginResolver, reports, scopePerDevice);
        }

        return new ChainCompilation(document, document.Render(), reports);
    }

    private static void CompileDevice(
        ApoDocument document,
        DeviceProfile profile,
        SoundstageState state,
        Func<string, EqPreset?> presetResolver,
        Func<int, string?>? ambienceIrResolver,
        Func<string, string?>? vstPluginResolver,
        List<DeviceChainReport> reports,
        bool scopePerDevice)
    {
        var notes = new List<string>();
        var capabilities = profile.Capabilities;
        var preset = profile.ActivePresetId is null ? null : presetResolver(profile.ActivePresetId);
        if (profile.ActivePresetId is not null && preset is null)
        {
            notes.Add($"Preset '{profile.ActivePresetId}' not found; compiling without EQ.");
        }

        var effects = profile.Effects;
        var night = EffectCompilers.CompileNightMode(effects.NightMode);
        var fidelity = EffectCompilers.CompileFidelity(effects.Fidelity);
        var loudness = EffectCompilers.CompileLoudness(effects.Loudness);
        var width = EffectCompilers.CompileStereoWidth(effects.StereoWidth, capabilities);
        var ambience = EffectCompilers.CompileAmbience(effects.Ambience, capabilities, ambienceIrResolver);

        foreach (var note in new[] { night.Note, fidelity.Note, loudness.Note, width.Note, ambience.Note })
        {
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        // Everything contributing frequency-dependent gain goes through the analyzer.
        var analyzedFilters = new List<FilterCommand>();
        IReadOnlyList<GraphicEqPoint>? graphicPoints = null;
        if (preset is not null)
        {
            foreach (var command in preset.ToApoCommands())
            {
                switch (command)
                {
                    case FilterCommand f:
                        analyzedFilters.Add(f);
                        break;
                    case GraphicEqCommand g:
                        graphicPoints = g.Points;
                        break;
                }
            }
        }

        analyzedFilters.AddRange(night.Commands.OfType<FilterCommand>());
        analyzedFilters.AddRange(fidelity.Commands.OfType<FilterCommand>());

        var authorPreamp = preset?.PreampDb ?? 0;
        var effectiveAuthorPreamp = authorPreamp - night.ExtraHeadroomDb - ambience.ExtraHeadroomDb;
        var headroom = HeadroomAnalyzer.Analyze(
            effectiveAuthorPreamp,
            analyzedFilters,
            graphicPoints,
            broadbandGainsDb: [width.BroadbandGainDb],
            sampleRate: capabilities.SampleRateHz,
            safetyMarginDb: state.Settings.SafetyMarginDb,
            applyPeakTrim: state.Settings.ClippingProtection);

        // ---- Emit the section ----
        document.Commands.Add(new CommentCommand($"── {profile.FriendlyName} ({FormatChannels(capabilities.Channels)}) — preset: {preset?.Name ?? "none"}"));
        if (scopePerDevice)
        {
            document.Commands.Add(new DeviceCommand(profile.EffectiveMatchSpec));
        }

        if (Math.Abs(headroom.RecommendedPreampDb) > 0.01)
        {
            document.Commands.Add(new PreampCommand(headroom.RecommendedPreampDb));
        }

        if (preset is not null)
        {
            foreach (var command in preset.ToApoCommands())
            {
                document.Commands.Add(command);
            }
        }

        AppendCommands(document, night.Commands);
        AppendCommands(document, fidelity.Commands);
        AppendCommands(document, width.Commands);
        AppendCommands(document, ambience.Commands);
        AppendCommands(document, loudness.Commands);
        AppendVstRack(document, profile, capabilities.Channels, vstPluginResolver);
        AppendSpeakerCalibration(document, profile, capabilities.Channels);

        reports.Add(new DeviceChainReport(profile.EndpointId, profile.FriendlyName, preset?.Name, headroom, notes));
    }

    private static void AppendCommands(ApoDocument document, IReadOnlyList<ApoCommand> commands)
    {
        foreach (var command in commands)
        {
            document.Commands.Add(command);
        }
    }

    /// <summary>Resolves the device's enabled VST rack effects against the catalog and emits them.</summary>
    private static void AppendVstRack(ApoDocument document, DeviceProfile profile, int channels, Func<string, string?>? vstPluginResolver)
    {
        if (vstPluginResolver is null || profile.VstRack.Count == 0)
        {
            return;
        }

        var active = profile.VstRack
            .Where(e => e.Enabled)
            .Select(e => (Effect: VstCatalog.Get(e.Id), e.Intensity))
            .Where(x => x.Effect is not null)
            .Select(x => (x.Effect!, x.Intensity))
            .ToList();

        AppendCommands(document, EffectCompilers.CompileVstRack(active, channels, vstPluginResolver));
    }

    /// <summary>
    /// Emits per-speaker level trims as channel-scoped preamp cuts, last in the device section.
    /// Attenuation only (a cut can't clip; a trim on an absent channel is a no-op), then the channel
    /// scope is reset to the full layout so nothing downstream inherits a narrowed selection.
    /// </summary>
    private static void AppendSpeakerCalibration(ApoDocument document, DeviceProfile profile, int channels)
    {
        var trims = profile.SpeakerTrims.Where(t => t.TrimDb < -0.05).ToList();
        if (trims.Count == 0)
        {
            return;
        }

        document.Commands.Add(new CommentCommand("Speaker calibration (per-channel level)"));
        foreach (var trim in trims)
        {
            document.Commands.Add(new ChannelCommand(trim.Channel));
            document.Commands.Add(new PreampCommand(trim.TrimDb));
        }

        var reset = SpeakerLayout.ResetSpec(channels);
        if (!string.IsNullOrEmpty(reset))
        {
            document.Commands.Add(new ChannelCommand(reset));
        }
    }

    public static string FormatChannels(int channels) => channels switch
    {
        1 => "mono",
        2 => "2.0",
        4 => "4.0",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels} ch",
    };
}
