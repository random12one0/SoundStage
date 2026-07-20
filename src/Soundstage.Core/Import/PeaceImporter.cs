using System.Text.RegularExpressions;
using Soundstage.Core.Apo;
using Soundstage.Core.Presets;

namespace Soundstage.Core.Import;

/// <summary>
/// Imports presets exported from Peace GUI.
///
/// Peace's "export to text" produces plain Equalizer APO syntax (handled directly).
/// Peace's own <c>.peace</c> files are INI-flavored (<c>key=value</c> lines) and — because
/// Peace runs under the user's locale — may carry comma decimals. This importer is
/// deliberately forgiving: it extracts anything that looks like a filter line, normalizes
/// comma decimals between digits, and reports what it skipped.
/// </summary>
public static partial class PeaceImporter
{
    public sealed record ImportOutcome(EqPreset Preset, IReadOnlyList<string> Warnings);

    [GeneratedRegex(@"(\d),(\d)")]
    private static partial Regex CommaDecimalRegex();

    [GeneratedRegex(@"(?<state>ON|OFF)\s+(?<rest>(?:PK|PEQ|LSC?|HSC?|LPQ?|HPQ?|NO|BP|AP|None)\b.*)", RegexOptions.IgnoreCase)]
    private static partial Regex FilterFragmentRegex();

    [GeneratedRegex(@"^\s*Preamp\s*[:=]\s*(?<value>-?[\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PreampRegex();

    public static ImportOutcome Import(string text, string name)
    {
        var preset = new EqPreset
        {
            Id = PresetStore.Slugify(name),
            Name = name,
            Category = PresetCategories.Imported,
            Mode = EqMode.Parametric,
            SourceNote = "Peace import",
        };

        var warnings = new List<string>();
        var normalized = CommaDecimalRegex().Replace(text.Replace("\r\n", "\n"), "$1.$2");

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith('['))
            {
                continue;
            }

            var preampMatch = PreampRegex().Match(line);
            if (preampMatch.Success && ApoFormat.TryParseNum(preampMatch.Groups["value"].Value, out var preampDb))
            {
                preset.PreampDb = preampDb;
                continue;
            }

            var fragment = FilterFragmentRegex().Match(line);
            if (!fragment.Success)
            {
                continue;
            }

            // Rebuild a canonical APO filter line and reuse the strict parser.
            var candidate = $"Filter: {fragment.Groups["state"].Value.ToUpperInvariant()} {fragment.Groups["rest"].Value}";
            var parsed = ApoConfigParser.Parse(candidate);
            if (parsed.Document.Commands.Count == 1 && parsed.Document.Commands[0] is FilterCommand filter)
            {
                preset.Bands.Add(new EqBand(
                    filter.Type,
                    filter.FrequencyHz,
                    filter.GainDb,
                    filter.Q ?? Dsp.Biquad.DefaultQ(filter.Type),
                    filter.Enabled));
            }
            else if (!fragment.Groups["rest"].Value.StartsWith("None", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped unrecognized filter line: {line}");
            }
        }

        if (preset.Bands.Count == 0)
        {
            warnings.Add("No filters found in the Peace file.");
        }

        return new ImportOutcome(preset, warnings);
    }
}
