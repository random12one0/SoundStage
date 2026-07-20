using System.Reflection;
using System.Text.Json;
using Soundstage.Core.Apo;
using Soundstage.Core.Presets;

namespace Soundstage.Core.Import;

public sealed record AutoEqFilter(string TypeCode, double FrequencyHz, double GainDb, double Q);

public sealed record AutoEqEntry(string Model, string Source, double PreampDb, IReadOnlyList<AutoEqFilter> Filters)
{
    public string DisplayName => $"{Model} ({Source})";

    public EqPreset ToPreset()
    {
        var preset = new EqPreset
        {
            Id = PresetStore.Slugify($"{Model} {Source}"),
            Name = Model,
            Category = PresetCategories.Headphones,
            Mode = EqMode.Parametric,
            PreampDb = PreampDb,
            SourceNote = $"AutoEq ({Source})",
            Description = $"Headphone correction for {Model}, measured by {Source}. From the AutoEq project (MIT licensed).",
        };

        foreach (var f in Filters)
        {
            if (FilterTypeExtensions.TryFromApoCode(f.TypeCode, out var type))
            {
                preset.Bands.Add(new EqBand(type, f.FrequencyHz, f.GainDb, f.Q));
            }
        }

        return preset;
    }
}

/// <summary>
/// The bundled AutoEq headphone-correction database: thousands of models, searchable by
/// name, zero downloads at runtime. Packed at development time by tools/AutoEqPacker from
/// github.com/jaakkopasanen/AutoEq (MIT).
/// </summary>
public sealed class AutoEqCatalog
{
    private const string ResourceName = "Soundstage.Core.EmbeddedAssets.autoeq.autoeq.min.json";

    private readonly List<AutoEqEntry> _entries;

    public AutoEqCatalog(IEnumerable<AutoEqEntry> entries)
    {
        _entries = entries.ToList();
    }

    public int Count => _entries.Count;

    public bool IsAvailable => _entries.Count > 0;

    public IReadOnlyList<AutoEqEntry> Entries => _entries;

    /// <summary>Loads the embedded catalog; returns an empty catalog when the resource is absent.</summary>
    public static AutoEqCatalog LoadEmbedded()
    {
        using var stream = typeof(AutoEqCatalog).Assembly.GetManifestResourceStream(ResourceName);
        return stream is null ? new AutoEqCatalog([]) : Load(stream);
    }

    public static AutoEqCatalog Load(Stream jsonStream)
    {
        using var document = JsonDocument.Parse(jsonStream);
        var entries = new List<AutoEqEntry>();

        if (document.RootElement.TryGetProperty("entries", out var entriesElement))
        {
            foreach (var e in entriesElement.EnumerateArray())
            {
                var model = e.GetProperty("n").GetString();
                if (string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                var source = e.TryGetProperty("s", out var s) ? s.GetString() ?? "AutoEq" : "AutoEq";
                var preamp = e.TryGetProperty("p", out var p) ? p.GetDouble() : 0;

                var filters = new List<AutoEqFilter>();
                if (e.TryGetProperty("f", out var filterArray))
                {
                    foreach (var f in filterArray.EnumerateArray())
                    {
                        filters.Add(new AutoEqFilter(
                            f[0].GetString() ?? "PK",
                            f[1].GetDouble(),
                            f[2].GetDouble(),
                            f[3].GetDouble()));
                    }
                }

                entries.Add(new AutoEqEntry(model, source, preamp, filters));
            }
        }

        return new AutoEqCatalog(entries);
    }

    /// <summary>
    /// Token search over model + source: every query token must appear; results ranked by
    /// how tightly the model name matches (exact → prefix → contains).
    /// </summary>
    public IReadOnlyList<AutoEqEntry> Search(string query, int limit = 50)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToArray();
        if (tokens.Length == 0)
        {
            return [];
        }

        var normalizedQuery = string.Join(' ', tokens);
        var ranked = new List<(AutoEqEntry Entry, int Rank)>();

        foreach (var entry in _entries)
        {
            var model = entry.Model.ToLowerInvariant();
            var source = entry.Source.ToLowerInvariant();

            var allMatch = tokens.All(t => model.Contains(t) || source.Contains(t));
            if (!allMatch)
            {
                continue;
            }

            var rank = model == normalizedQuery ? 0
                : model.StartsWith(normalizedQuery, StringComparison.Ordinal) ? 1
                : model.Contains(normalizedQuery, StringComparison.Ordinal) ? 2
                : 3;
            ranked.Add((entry, rank));
        }

        return ranked
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Entry.Model.Length)
            .ThenBy(r => r.Entry.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Entry.Source, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(r => r.Entry)
            .ToList();
    }
}
