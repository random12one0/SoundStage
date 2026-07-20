using Soundstage.Core.Apo;

namespace Soundstage.Core.Effects;

/// <summary>Capabilities of the device a chain section targets.</summary>
public sealed record DeviceCapabilities(int Channels, int SampleRateHz);

/// <summary>
/// Output of one effect's compilation. <see cref="Commands"/> join the chain in the
/// effect's natural slot; <see cref="PostCommands"/> are emitted at the very end of the
/// device section (used by night mode's compressor, which must see the final signal).
/// </summary>
public sealed record EffectCompilation(
    IReadOnlyList<ApoCommand> Commands,
    IReadOnlyList<ApoCommand>? PostCommandsOrNull = null,
    double ExtraHeadroomDb = 0,
    double BroadbandGainDb = 0,
    string? Note = null)
{
    public static readonly EffectCompilation Empty = new([]);

    public IReadOnlyList<ApoCommand> PostCommands => PostCommandsOrNull ?? [];
}

/// <summary>
/// Pure translators from effect settings to APO commands. Each enforces its own guards;
/// the <c>ChainCompiler</c> stitches results together and feeds gain contributions to the
/// headroom analyzer.
/// </summary>
public static class EffectCompilers
{
    /// <summary>
    /// Night mode. The shelf handles what compression cannot (steady-state bass energy
    /// through walls); the compressor tames peaks. Without a configured VST compressor we
    /// degrade honestly: shelf + extra preamp headroom, flagged in the note.
    /// </summary>
    public static EffectCompilation CompileNightMode(NightModeSettings settings)
    {
        if (!settings.Enabled)
        {
            return EffectCompilation.Empty;
        }

        var commands = new List<ApoCommand>();
        var cut = settings.EffectiveBassCutDb;
        if (cut < -0.05)
        {
            commands.Add(new CommentCommand("Night mode: bass shelf"));
            commands.Add(new FilterCommand(FilterType.LowShelf, settings.BassCornerHz, cut, 0.707));
        }

        var hasVst = settings.UseVstCompressor && !string.IsNullOrWhiteSpace(settings.VstLibraryPath);
        if (hasVst)
        {
            return new EffectCompilation(commands, PostCommandsOrNull:
            [
                new CommentCommand("Night mode: compressor (VST)"),
                new VstPluginCommand(settings.VstLibraryPath!, settings.VstRawArguments),
            ]);
        }

        return new EffectCompilation(
            commands,
            ExtraHeadroomDb: settings.EffectiveDegradedHeadroomDb,
            Note: "Compression off — no VST compressor configured; using bass shelf + extra headroom.");
    }

    public static EffectCompilation CompileLoudness(LoudnessSettings settings)
    {
        if (!settings.Enabled)
        {
            return EffectCompilation.Empty;
        }

        return new EffectCompilation([
            new CommentCommand("Loudness compensation"),
            new LoudnessCorrectionCommand(true, settings.EffectiveReferenceLevel, settings.AttenuationDb),
        ]);
    }

    /// <summary>
    /// Stereo width as a channel matrix. Hard guard: only compiles for exactly 2 output
    /// channels — on 5.1/7.1 the L/R matrix would corrupt the surround image, so it emits
    /// nothing and says why.
    /// </summary>
    public static EffectCompilation CompileStereoWidth(StereoWidthSettings settings, DeviceCapabilities capabilities)
    {
        if (!settings.Enabled)
        {
            return EffectCompilation.Empty;
        }

        if (capabilities.Channels != 2)
        {
            return EffectCompilation.Empty with { Note = $"Stereo width skipped: device is {capabilities.Channels}-channel, effect is stereo-only." };
        }

        var w = settings.Width;
        if (Math.Abs(w - 1.0) < 0.005)
        {
            return EffectCompilation.Empty;
        }

        // L' = a·L + b·R, R' = b·L + a·R with a=(1+w)/2, b=(1−w)/2.
        // Mono (in-phase) content: a+b = 1 → untouched. Side content scales by w.
        var a = (1 + w) / 2.0;
        var b = (1 - w) / 2.0;
        var commands = new List<ApoCommand>
        {
            new CommentCommand($"Stereo width {settings.WidthPercent}%"),
            new CopyCommand([
                new CopyAssignment("L", [new CopyTerm(a, "L"), new CopyTerm(b, "R")]),
                new CopyAssignment("R", [new CopyTerm(b, "L"), new CopyTerm(a, "R")]),
            ]),
        };

        return new EffectCompilation(commands, BroadbandGainDb: settings.MaxGainDb);
    }

    /// <summary>
    /// Ambience: a short convolution reverb using a generated impulse response. Only emits
    /// when the IR file for the device's sample rate is known to exist (the app generates
    /// them; headless/tests pass a probe).
    /// </summary>
    public static EffectCompilation CompileAmbience(AmbienceSettings settings, DeviceCapabilities capabilities, Func<int, string?>? irPathResolver)
    {
        if (!settings.Enabled || irPathResolver is null)
        {
            return EffectCompilation.Empty;
        }

        var path = irPathResolver(capabilities.SampleRateHz);
        if (path is null)
        {
            return EffectCompilation.Empty with { Note = "Ambience skipped: no impulse response for this sample rate." };
        }

        return new EffectCompilation([
            new CommentCommand($"Ambience {settings.Intensity}%"),
            new ConvolutionCommand(path),
        ]);
    }
}
