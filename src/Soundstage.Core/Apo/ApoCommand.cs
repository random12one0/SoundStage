using System.Text;

namespace Soundstage.Core.Apo;

/// <summary>
/// One line of an Equalizer APO configuration. The AST is deliberately line-oriented:
/// APO's parser is line-based, and keeping a 1:1 mapping makes round-tripping and
/// golden-file testing exact.
/// </summary>
public abstract record ApoCommand
{
    /// <summary>Renders the APO config line for this command (no line terminator).</summary>
    public abstract string Render();
}

/// <summary>A <c># comment</c> line.</summary>
public sealed record CommentCommand(string Text) : ApoCommand
{
    public override string Render() => Text.Length == 0 ? "#" : $"# {Text}";
}

/// <summary>An empty line (kept for readable generated configs).</summary>
public sealed record BlankLineCommand : ApoCommand
{
    public static readonly BlankLineCommand Instance = new();

    public override string Render() => string.Empty;
}

/// <summary>A line preserved verbatim — unknown or hand-written content we must not disturb.</summary>
public sealed record RawCommand(string Line) : ApoCommand
{
    public override string Render() => Line;
}

/// <summary><c>Preamp: -6.0 dB</c></summary>
public sealed record PreampCommand(double Db) : ApoCommand
{
    public override string Render() => $"Preamp: {ApoFormat.Gain(Db)} dB";
}

/// <summary>
/// <c>Filter: ON PK Fc 1000 Hz Gain -3.0 dB Q 1.41</c>
/// Rendered without an index by default; <see cref="ApoDocument"/> assigns sequential
/// numbers per device section when rendering a full document, matching the convention
/// Peace and AutoEq use.
/// </summary>
public sealed record FilterCommand(FilterType Type, double FrequencyHz, double GainDb = 0, double? Q = null, bool Enabled = true) : ApoCommand
{
    public override string Render() => RenderNumbered(null);

    public string RenderNumbered(int? number)
    {
        var sb = new StringBuilder();
        sb.Append(number is null ? "Filter: " : $"Filter {number}: ");
        sb.Append(Enabled ? "ON " : "OFF ");
        sb.Append(Type.ToApoCode());
        sb.Append(" Fc ").Append(ApoFormat.Num(FrequencyHz)).Append(" Hz");
        if (Type.UsesGain())
        {
            sb.Append(" Gain ").Append(ApoFormat.Gain(GainDb)).Append(" dB");
        }

        if (Q is { } q)
        {
            sb.Append(" Q ").Append(ApoFormat.Num(q));
        }

        return sb.ToString();
    }
}

/// <summary><c>GraphicEQ: 25 -2.5; 40 -1.2; …</c> — APO interpolates between the points.</summary>
public sealed record GraphicEqCommand(IReadOnlyList<GraphicEqPoint> Points) : ApoCommand
{
    public override string Render() =>
        "GraphicEQ: " + string.Join("; ", Points.Select(p => $"{ApoFormat.Num(p.FrequencyHz)} {ApoFormat.Gain(p.GainDb)}"));
}

public readonly record struct GraphicEqPoint(double FrequencyHz, double GainDb);

/// <summary>One term of a <c>Copy</c> assignment: <c>0.5*L</c>, <c>R</c>, or a constant.</summary>
public readonly record struct CopyTerm(double Coefficient, string? SourceChannel)
{
    public string Render()
    {
        if (SourceChannel is null)
        {
            return ApoFormat.Coefficient(Coefficient);
        }

        return Math.Abs(Coefficient - 1.0) < 1e-12
            ? SourceChannel
            : $"{ApoFormat.Coefficient(Coefficient)}*{SourceChannel}";
    }
}

/// <summary>One assignment of a <c>Copy</c> command: <c>L=0.75*L+0.25*R</c>.</summary>
public sealed record CopyAssignment(string TargetChannel, IReadOnlyList<CopyTerm> Terms)
{
    public string Render()
    {
        var sb = new StringBuilder(TargetChannel);
        sb.Append('=');
        for (var i = 0; i < Terms.Count; i++)
        {
            var rendered = Terms[i].Render();
            if (i > 0 && !rendered.StartsWith('-'))
            {
                sb.Append('+');
            }

            sb.Append(rendered);
        }

        return sb.ToString();
    }
}

/// <summary><c>Copy: L=0.75*L+0.25*R R=0.25*L+0.75*R</c> — channel mixing matrix.</summary>
public sealed record CopyCommand(IReadOnlyList<CopyAssignment> Assignments) : ApoCommand
{
    public override string Render() => "Copy: " + string.Join(" ", Assignments.Select(a => a.Render()));
}

/// <summary><c>Delay: 5.2 ms</c></summary>
public sealed record DelayCommand(double Milliseconds) : ApoCommand
{
    public override string Render() => $"Delay: {ApoFormat.Num(Milliseconds)} ms";
}

/// <summary><c>Convolution: path\to\impulse.wav</c> (path relative to the config file's directory).</summary>
public sealed record ConvolutionCommand(string Path) : ApoCommand
{
    public override string Render() => $"Convolution: {Path}";
}

/// <summary>
/// <c>Device: {guid} …</c> — scopes all following lines (until the next Device line) to
/// matching audio endpoints. The match spec is stored verbatim.
/// </summary>
public sealed record DeviceCommand(string MatchSpec) : ApoCommand
{
    public override string Render() => $"Device: {MatchSpec}";
}

/// <summary><c>Channel: L R</c> — scopes following lines to specific channels.</summary>
public sealed record ChannelCommand(string Spec) : ApoCommand
{
    public override string Render() => $"Channel: {Spec}";
}

/// <summary><c>Include: file.txt</c> — path relative to the including file's directory.</summary>
public sealed record IncludeCommand(string Path) : ApoCommand
{
    public override string Render() => $"Include: {Path}";
}

/// <summary>
/// <c>LoudnessCorrection: State 1 ReferenceLevel -22 Attenuation 0</c> — Equalizer APO's
/// built-in volume-tracking equal-loudness correction.
/// </summary>
public sealed record LoudnessCorrectionCommand(bool State, double ReferenceLevel, double Attenuation) : ApoCommand
{
    public override string Render() =>
        $"LoudnessCorrection: State {(State ? 1 : 0)} ReferenceLevel {ApoFormat.Num(ReferenceLevel)} Attenuation {ApoFormat.Num(Attenuation)}";
}

/// <summary>
/// <c>VSTPlugin: Library "C:\path\plugin.dll"</c> — hosts a VST 2 plugin (Equalizer APO 1.3+).
/// <paramref name="RawArguments"/> is appended verbatim when present (e.g. an inline
/// parameter list captured from a config authored by the APO Editor).
/// </summary>
public sealed record VstPluginCommand(string LibraryPath, string? RawArguments = null) : ApoCommand
{
    public override string Render()
    {
        var line = $"VSTPlugin: Library \"{LibraryPath}\"";
        return string.IsNullOrWhiteSpace(RawArguments) ? line : $"{line} {RawArguments}";
    }
}
