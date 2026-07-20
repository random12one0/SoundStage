using Soundstage.Core.Apo;
using Soundstage.Core.Presets;

namespace Soundstage.Core.Import;

/// <summary>
/// Imports AutoEq's ParametricEQ.txt output — which is already Equalizer APO syntax —
/// into a Soundstage preset.
/// </summary>
public static class AutoEqImporter
{
    public sealed record ImportOutcome(EqPreset Preset, IReadOnlyList<string> Warnings);

    public static ImportOutcome Import(string apoText, string name, string? sourceNote = null)
    {
        var parsed = ApoConfigParser.Parse(apoText);
        var preset = new EqPreset
        {
            Id = PresetStore.Slugify(name),
            Name = name,
            Category = PresetCategories.Headphones,
            Mode = EqMode.Parametric,
            SourceNote = sourceNote ?? "AutoEq import",
        };

        var warnings = new List<string>(parsed.Warnings);
        foreach (var command in parsed.Document.Commands)
        {
            switch (command)
            {
                case PreampCommand preamp:
                    preset.PreampDb = preamp.Db;
                    break;
                case FilterCommand filter:
                    preset.Bands.Add(new EqBand(
                        filter.Type,
                        filter.FrequencyHz,
                        filter.GainDb,
                        filter.Q ?? Dsp.Biquad.DefaultQ(filter.Type),
                        filter.Enabled));
                    break;
                case GraphicEqCommand:
                    warnings.Add("File contains a GraphicEQ line; import it as a graphic preset via the Peace importer instead.");
                    break;
            }
        }

        if (preset.Bands.Count == 0)
        {
            warnings.Add("No filters found — is this a ParametricEQ.txt export?");
        }

        return new ImportOutcome(preset, warnings);
    }
}
