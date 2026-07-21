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
    /// Night mode — 100% native, no plugins. Equalizer APO has no built-in dynamics processor,
    /// so night mode does the two things that reliably help at night without hosting anything:
    /// a low-shelf <b>bass cut</b> (low frequencies are what travel through walls) plus a little
    /// overall <b>level reduction</b> (extra preamp headroom) to take the edge off loud peaks.
    /// Because nothing external is in the path, it can never fail to load and silence the stream.
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
            commands.Add(new FilterCommand(FilterType.LowShelf, settings.EffectiveCornerHz, cut, 0.707));
        }

        return new EffectCompilation(commands, ExtraHeadroomDb: settings.EffectiveLevelReductionDb);
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
    /// Stereo width as a front-L/R mid/side matrix — it amplifies the left/right difference that is
    /// already in the recording (never routes audio to different speakers). The <c>Copy</c> commands
    /// reassign only the L and R channels, so it is safe on any layout — a 5.1/7.1 centre, LFE and
    /// surrounds pass through untouched. The only guard is mono output (no R channel to mix).
    /// </summary>
    public static EffectCompilation CompileStereoWidth(StereoWidthSettings settings, DeviceCapabilities capabilities)
    {
        if (!settings.Enabled)
        {
            return EffectCompilation.Empty;
        }

        if (capabilities.Channels < 2)
        {
            return EffectCompilation.Empty with { Note = "Stereo width needs at least two channels (mono output)." };
        }

        var w = settings.Width;
        if (Math.Abs(w - 1.0) < 0.005)
        {
            return EffectCompilation.Empty; // 0% → untouched
        }

        // Mid/side widening as a SINGLE-LINE direct matrix — no scratch/virtual channels.
        // Equalizer APO evaluates every assignment on one Copy line in parallel (each right-hand
        // side reads the ORIGINAL pre-Copy value), so `L=a*L+b*R  R=a*R+b*L` is a correct
        // simultaneous 2×2 matrix. Expanding L'=mid+w·side, R'=mid−w·side gives a=(1+w)/2,
        // b=(1−w)/2. This was the fix for "everything collapsed into one speaker": the previous
        // version created named virtual channels (SS_MID/SS_SIDE), which on a fixed 7.1 device is
        // exactly the kind of thing that misroutes the real channels. This names ONLY L and R, so a
        // 5.1/7.1 centre, LFE and surrounds pass straight through, and it invents nothing. w is
        // capped at MaxWidthFactor so the anti-phase term can never dominate. All coefficients are
        // fractional and rendered with InvariantCulture, so APO never mistakes one for a channel index.
        var a = (1.0 + w) / 2.0;
        var b = (1.0 - w) / 2.0;
        var commands = new List<ApoCommand>
        {
            new CommentCommand($"Stereo width {settings.WidthPercent}% — amplifies existing L/R separation (front L/R only)"),
            new CopyCommand([
                new CopyAssignment("L", [new CopyTerm(a, "L"), new CopyTerm(b, "R")]),
                new CopyAssignment("R", [new CopyTerm(a, "R"), new CopyTerm(b, "L")]),
            ]),
        };

        return new EffectCompilation(commands, BroadbandGainDb: settings.MaxGainDb);
    }

    /// <summary>
    /// Fidelity — a fixed "clarity" contour: a presence peak (~3.5 kHz) for detail plus an air
    /// high-shelf (~12 kHz) for openness, both scaled by intensity. Pure EQ boosts, so the chain
    /// compiler feeds them to the headroom analyzer and clipping protection covers them.
    /// </summary>
    public static EffectCompilation CompileFidelity(FidelitySettings settings)
    {
        if (!settings.Enabled)
        {
            return EffectCompilation.Empty;
        }

        var presence = settings.PresenceDb;
        var air = settings.AirDb;
        if (presence < 0.05 && air < 0.05)
        {
            return EffectCompilation.Empty;
        }

        var commands = new List<ApoCommand> { new CommentCommand($"Fidelity {settings.Intensity}% — presence + air lift") };
        if (presence >= 0.05)
        {
            commands.Add(new FilterCommand(FilterType.Peaking, FidelitySettings.PresenceHz, presence, FidelitySettings.PresenceQ));
        }

        if (air >= 0.05)
        {
            commands.Add(new FilterCommand(FilterType.HighShelf, FidelitySettings.AirHz, air, FidelitySettings.AirQ));
        }

        return new EffectCompilation(commands);
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

        return new EffectCompilation(
            [
                new CommentCommand($"Ambience {settings.Intensity}%"),
                new ConvolutionCommand(path),
            ],
            ExtraHeadroomDb: IrGenerator.ExtraHeadroomDbFor(settings.Intensity));
    }
}
