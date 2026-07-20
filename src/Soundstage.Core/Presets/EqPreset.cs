using Soundstage.Core.Apo;

namespace Soundstage.Core.Presets;

public enum EqMode
{
    Parametric,
    Graphic10,
    Graphic31,
}

/// <summary>One parametric EQ band of a preset.</summary>
public sealed record EqBand(FilterType Type, double FrequencyHz, double GainDb, double Q, bool Enabled = true);

/// <summary>Standard graphic-EQ center frequencies.</summary>
public static class GraphicBands
{
    public static readonly double[] Freq10 = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static readonly double[] Freq31 =
    [
        20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160,
        200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000,
        20000,
    ];

    public static double[] FrequenciesFor(EqMode mode) => mode switch
    {
        EqMode.Graphic10 => Freq10,
        EqMode.Graphic31 => Freq31,
        _ => throw new ArgumentException($"Not a graphic mode: {mode}", nameof(mode)),
    };
}

/// <summary>
/// A named EQ curve. Parametric presets carry bands; graphic presets carry per-band gains
/// aligned with <see cref="GraphicBands"/>. Author preamp is the preset creator's intent;
/// the compiler may trim further for clipping safety.
/// </summary>
public sealed class EqPreset
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>"Speakers", "Headphones", "Custom", "Imported".</summary>
    public string Category { get; set; } = PresetCategories.Custom;

    public EqMode Mode { get; set; } = EqMode.Parametric;

    public List<EqBand> Bands { get; set; } = [];

    /// <summary>Gains for graphic modes; length matches the mode's band count.</summary>
    public double[] GraphicGains { get; set; } = [];

    public double PreampDb { get; set; }

    public bool IsBuiltIn { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>Provenance for imported presets ("AutoEq: Sennheiser HD 650", "Peace import").</summary>
    public string? SourceNote { get; set; }

    public IEnumerable<EqBand> EnabledBands => Bands.Where(b => b.Enabled);

    public static EqPreset CreateFlat(string id = "flat", string name = "Flat Reference") => new()
    {
        Id = id,
        Name = name,
        Category = PresetCategories.Speakers,
        Mode = EqMode.Parametric,
        IsBuiltIn = true,
        Description = "No processing — the untouched output of your source and receiver.",
    };

    public EqPreset Clone(string newId, string newName) => new()
    {
        Id = newId,
        Name = newName,
        Description = Description,
        Category = Category,
        Mode = Mode,
        Bands = [.. Bands],
        GraphicGains = [.. GraphicGains],
        PreampDb = PreampDb,
        IsBuiltIn = false,
        ModifiedUtc = ModifiedUtc,
        SourceNote = SourceNote,
    };

    /// <summary>The preset's EQ content as APO commands (no preamp — the compiler owns preamp).</summary>
    public IReadOnlyList<ApoCommand> ToApoCommands()
    {
        if (Mode == EqMode.Parametric)
        {
            return EnabledBands
                .Select(b => (ApoCommand)new FilterCommand(b.Type, b.FrequencyHz, b.GainDb, b.Q))
                .ToList();
        }

        var freqs = GraphicBands.FrequenciesFor(Mode);
        var points = new List<GraphicEqPoint>(freqs.Length);
        for (var i = 0; i < freqs.Length; i++)
        {
            var gain = i < GraphicGains.Length ? GraphicGains[i] : 0;
            points.Add(new GraphicEqPoint(freqs[i], gain));
        }

        return [new GraphicEqCommand(points)];
    }
}

public static class PresetCategories
{
    public const string Speakers = "Speakers";
    public const string Headphones = "Headphones";
    public const string Custom = "Custom";
    public const string Imported = "Imported";
}
