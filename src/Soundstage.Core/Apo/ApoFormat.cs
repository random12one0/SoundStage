using System.Globalization;

namespace Soundstage.Core.Apo;

/// <summary>
/// Number formatting for APO config lines. Everything must be rendered with
/// <see cref="CultureInfo.InvariantCulture"/> — Equalizer APO parses <c>.</c> decimals only,
/// so a machine running under a comma-decimal locale must never leak its culture here.
/// </summary>
internal static class ApoFormat
{
    /// <summary>General numeric value (frequencies, Q, coefficients): up to 4 decimals, no trailing zeros.</summary>
    public static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Gain in dB: always at least one decimal (Peace/AutoEq convention), up to two.</summary>
    public static string Gain(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);

    /// <summary>Copy-matrix coefficient: up to 6 decimals for matrix precision.</summary>
    public static string Coefficient(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static bool TryParseNum(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
