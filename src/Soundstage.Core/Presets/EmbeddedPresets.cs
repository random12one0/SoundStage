using System.Reflection;
using System.Text.Json;

namespace Soundstage.Core.Presets;

/// <summary>Loads the curated built-in presets embedded in this assembly.</summary>
public static class EmbeddedPresets
{
    private const string Prefix = "Soundstage.Core.EmbeddedAssets.presets.";

    /// <summary>Curated display order (files not listed sort after, alphabetically).</summary>
    private static readonly string[] Order =
    [
        "flat", "music", "bass-boost", "bass-reducer", "vocal", "rock", "electronic",
        "hip-hop", "jazz", "classical", "film-dialogue", "gaming", "podcast",
    ];

    public static IReadOnlyList<EqPreset> LoadAll()
    {
        var assembly = typeof(EmbeddedPresets).Assembly;
        var presets = new List<EqPreset>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(Prefix, StringComparison.Ordinal) || !resourceName.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            var preset = JsonSerializer.Deserialize<EqPreset>(stream, JsonDefaults.Readable);
            if (preset is not null)
            {
                preset.IsBuiltIn = true;
                presets.Add(preset);
            }
        }

        return presets
            .OrderBy(p =>
            {
                var index = Array.IndexOf(Order, p.Id);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
