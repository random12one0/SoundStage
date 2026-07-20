namespace Soundstage.Core.Apo;

/// <summary>Result of parsing APO config text: the document plus non-fatal warnings.</summary>
public sealed record ApoParseResult(ApoDocument Document, IReadOnlyList<string> Warnings);

/// <summary>
/// Tolerant line-based parser for Equalizer APO config syntax.
///
/// Structured parsing is implemented for the commands Soundstage needs to understand
/// (<c>Preamp</c>, <c>Filter</c>, <c>GraphicEQ</c>, <c>Device</c>, <c>Include</c>, comments);
/// every other line — including anything malformed — is preserved verbatim as a
/// <see cref="RawCommand"/> so that re-rendering never destroys hand-written content.
/// </summary>
public static class ApoConfigParser
{
    public static ApoParseResult Parse(string text)
    {
        var document = new ApoDocument();
        var warnings = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        // Drop a single trailing empty line produced by a terminating newline.
        var count = lines.Length;
        if (count > 0 && lines[count - 1].Length == 0)
        {
            count--;
        }

        for (var i = 0; i < count; i++)
        {
            document.Commands.Add(ParseLine(lines[i], i + 1, warnings));
        }

        return new ApoParseResult(document, warnings);
    }

    private static ApoCommand ParseLine(string rawLine, int lineNumber, List<string> warnings)
    {
        var line = rawLine.Trim();

        if (line.Length == 0)
        {
            return BlankLineCommand.Instance;
        }

        if (line.StartsWith('#'))
        {
            return new CommentCommand(line.TrimStart('#').Trim());
        }

        var colon = line.IndexOf(':');
        if (colon <= 0)
        {
            warnings.Add($"Line {lineNumber}: no command separator, kept verbatim.");
            return new RawCommand(rawLine);
        }

        var name = line[..colon].Trim();
        var args = line[(colon + 1)..].Trim();

        // "Filter 12" → "Filter"
        var nameParts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var commandName = nameParts[0];

        switch (commandName.ToLowerInvariant())
        {
            case "preamp":
                return ParsePreamp(args, rawLine, lineNumber, warnings);
            case "filter":
                return ParseFilter(args, rawLine, lineNumber, warnings);
            case "graphiceq":
                return ParseGraphicEq(args, rawLine, lineNumber, warnings);
            case "device":
                return new DeviceCommand(args);
            case "include":
                return new IncludeCommand(args);
            case "channel":
                return new ChannelCommand(args);
            default:
                return new RawCommand(rawLine);
        }
    }

    private static ApoCommand ParsePreamp(string args, string rawLine, int lineNumber, List<string> warnings)
    {
        // "−6.4 dB"
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 1 && ApoFormat.TryParseNum(tokens[0], out var db))
        {
            return new PreampCommand(db);
        }

        warnings.Add($"Line {lineNumber}: could not parse Preamp value, kept verbatim.");
        return new RawCommand(rawLine);
    }

    private static ApoCommand ParseFilter(string args, string rawLine, int lineNumber, List<string> warnings)
    {
        // "ON PK Fc 1000 Hz Gain -3.0 dB Q 1.41", "OFF LS 6dB Fc 100 Hz Gain 2 dB", "ON None"
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            warnings.Add($"Line {lineNumber}: incomplete Filter line, kept verbatim.");
            return new RawCommand(rawLine);
        }

        var index = 0;
        bool enabled;
        switch (tokens[index].ToUpperInvariant())
        {
            case "ON":
                enabled = true;
                break;
            case "OFF":
                enabled = false;
                break;
            default:
                warnings.Add($"Line {lineNumber}: Filter line missing ON/OFF, kept verbatim.");
                return new RawCommand(rawLine);
        }

        index++;

        if (tokens[index].Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            // A disabled placeholder slot (Peace writes these); nothing to model.
            return new RawCommand(rawLine);
        }

        if (!FilterTypeExtensions.TryFromApoCode(tokens[index], out var type))
        {
            warnings.Add($"Line {lineNumber}: unknown filter type '{tokens[index]}', kept verbatim.");
            return new RawCommand(rawLine);
        }

        index++;

        // Fixed-slope shelf variants: "LS 6dB", "HS 12dB" — the slope token is skipped.
        if (index < tokens.Length && (tokens[index].EndsWith("dB", StringComparison.OrdinalIgnoreCase)
                                      && ApoFormat.TryParseNum(tokens[index][..^2], out _)))
        {
            index++;
        }

        double? fc = null, gain = null, q = null;
        while (index < tokens.Length - 1)
        {
            var key = tokens[index].ToUpperInvariant();
            switch (key)
            {
                case "FC":
                    if (ApoFormat.TryParseNum(tokens[index + 1], out var f))
                    {
                        fc = f;
                    }

                    index += 2;
                    // Optional "Hz" unit token.
                    if (index < tokens.Length && tokens[index].Equals("Hz", StringComparison.OrdinalIgnoreCase))
                    {
                        index++;
                    }

                    break;
                case "GAIN":
                    if (ApoFormat.TryParseNum(tokens[index + 1], out var g))
                    {
                        gain = g;
                    }

                    index += 2;
                    if (index < tokens.Length && tokens[index].Equals("dB", StringComparison.OrdinalIgnoreCase))
                    {
                        index++;
                    }

                    break;
                case "Q":
                    if (ApoFormat.TryParseNum(tokens[index + 1], out var qv))
                    {
                        q = qv;
                    }

                    index += 2;
                    break;
                case "BW":
                    // Bandwidth variant ("BW Oct 0.5"): not modeled — keep the line raw so nothing is lost.
                    warnings.Add($"Line {lineNumber}: bandwidth (BW) filters are preserved but not editable.");
                    return new RawCommand(rawLine);
                default:
                    index++;
                    break;
            }
        }

        if (fc is null)
        {
            warnings.Add($"Line {lineNumber}: Filter line missing Fc, kept verbatim.");
            return new RawCommand(rawLine);
        }

        return new FilterCommand(type, fc.Value, gain ?? 0, q, enabled);
    }

    private static ApoCommand ParseGraphicEq(string args, string rawLine, int lineNumber, List<string> warnings)
    {
        var points = new List<GraphicEqPoint>();
        foreach (var pair in args.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !ApoFormat.TryParseNum(parts[0], out var freq)
                || !ApoFormat.TryParseNum(parts[1], out var gain))
            {
                warnings.Add($"Line {lineNumber}: could not parse GraphicEQ point '{pair.Trim()}', kept verbatim.");
                return new RawCommand(rawLine);
            }

            points.Add(new GraphicEqPoint(freq, gain));
        }

        if (points.Count == 0)
        {
            warnings.Add($"Line {lineNumber}: empty GraphicEQ, kept verbatim.");
            return new RawCommand(rawLine);
        }

        return new GraphicEqCommand(points);
    }
}
