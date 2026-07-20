namespace Soundstage.Core.Apo;

/// <summary>Biquad filter types supported by Equalizer APO's <c>Filter</c> command.</summary>
public enum FilterType
{
    Peaking,
    LowShelf,
    HighShelf,
    LowPass,
    HighPass,
    Notch,
    BandPass,
    AllPass,
}

public static class FilterTypeExtensions
{
    /// <summary>The type code Equalizer APO expects in a <c>Filter</c> line.</summary>
    public static string ToApoCode(this FilterType type) => type switch
    {
        FilterType.Peaking => "PK",
        FilterType.LowShelf => "LSC",
        FilterType.HighShelf => "HSC",
        FilterType.LowPass => "LP",
        FilterType.HighPass => "HP",
        FilterType.Notch => "NO",
        FilterType.BandPass => "BP",
        FilterType.AllPass => "AP",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// Maps an APO filter type code to a <see cref="FilterType"/>. Accepts the fixed-slope
    /// aliases (<c>LS</c>/<c>HS</c>) and the Q-based variants (<c>LSC</c>/<c>HSC</c>).
    /// </summary>
    public static bool TryFromApoCode(string code, out FilterType type)
    {
        switch (code.ToUpperInvariant())
        {
            case "PK":
            case "PEQ":
                type = FilterType.Peaking;
                return true;
            case "LS":
            case "LSC":
                type = FilterType.LowShelf;
                return true;
            case "HS":
            case "HSC":
                type = FilterType.HighShelf;
                return true;
            case "LP":
            case "LPQ":
                type = FilterType.LowPass;
                return true;
            case "HP":
            case "HPQ":
                type = FilterType.HighPass;
                return true;
            case "NO":
                type = FilterType.Notch;
                return true;
            case "BP":
                type = FilterType.BandPass;
                return true;
            case "AP":
                type = FilterType.AllPass;
                return true;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>Whether the APO syntax for this filter type carries a <c>Gain</c> value.</summary>
    public static bool UsesGain(this FilterType type) => type is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf;
}
