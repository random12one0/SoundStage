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
/// Section order per device: preamp → preset EQ → night-mode shelf → stereo width →
/// loudness correction → night-mode compressor (compressing the fully shaped signal last).
/// </summary>
public static class ChainCompiler
{
    public static ChainCompilation Compile(
        SoundstageState state,
        Func<string, EqPreset?> presetResolver,
        Func<int, string?>? ambienceIrResolver = null)
    {
        var document = new ApoDocument();
        var reports = new List<DeviceChainReport>();

        document.Commands.Add(new CommentCommand($"{ConfigLayout.ManagedMarker}. Generated processing chain — do not edit (use user.txt)."));

        var profiles = state.Profiles.Where(p => p.Enabled).ToList();
        if (profiles.Count == 0)
        {
            document.Commands.Add(new CommentCommand("No devices configured."));
        }

        foreach (var profile in profiles)
        {
            document.Commands.Add(BlankLineCommand.Instance);
            CompileDevice(document, profile, state, presetResolver, ambienceIrResolver, reports);
        }

        return new ChainCompilation(document, document.Render(), reports);
    }

    private static void CompileDevice(
        ApoDocument document,
        DeviceProfile profile,
        SoundstageState state,
        Func<string, EqPreset?> presetResolver,
        Func<int, string?>? ambienceIrResolver,
        List<DeviceChainReport> reports)
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
        var loudness = EffectCompilers.CompileLoudness(effects.Loudness);
        var width = EffectCompilers.CompileStereoWidth(effects.StereoWidth, capabilities);
        var ambience = state.Settings.AmbienceFeatureEnabled
            ? EffectCompilers.CompileAmbience(effects.Ambience, capabilities, ambienceIrResolver)
            : EffectCompilation.Empty;

        foreach (var note in new[] { night.Note, loudness.Note, width.Note, ambience.Note })
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

        var authorPreamp = preset?.PreampDb ?? 0;
        var effectiveAuthorPreamp = authorPreamp - night.ExtraHeadroomDb;
        var headroom = HeadroomAnalyzer.Analyze(
            effectiveAuthorPreamp,
            analyzedFilters,
            graphicPoints,
            broadbandGainsDb: [width.BroadbandGainDb],
            sampleRate: capabilities.SampleRateHz,
            safetyMarginDb: state.Settings.SafetyMarginDb);

        // ---- Emit the section ----
        document.Commands.Add(new CommentCommand($"── {profile.FriendlyName} ({FormatChannels(capabilities.Channels)}) — preset: {preset?.Name ?? "none"}"));
        document.Commands.Add(new DeviceCommand(profile.EffectiveMatchSpec));

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
        AppendCommands(document, width.Commands);
        AppendCommands(document, ambience.Commands);
        AppendCommands(document, loudness.Commands);

        // Compressor last: it should see the final, fully shaped signal.
        AppendCommands(document, night.PostCommands);

        reports.Add(new DeviceChainReport(profile.EndpointId, profile.FriendlyName, preset?.Name, headroom, notes));
    }

    private static void AppendCommands(ApoDocument document, IReadOnlyList<ApoCommand> commands)
    {
        foreach (var command in commands)
        {
            document.Commands.Add(command);
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
