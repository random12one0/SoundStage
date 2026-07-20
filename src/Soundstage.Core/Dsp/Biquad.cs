using Soundstage.Core.Apo;

namespace Soundstage.Core.Dsp;

/// <summary>Normalized biquad coefficients (a0 == 1).</summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2);

/// <summary>
/// RBJ Audio-EQ-Cookbook biquads — the same formulas Equalizer APO implements, used here
/// to predict the magnitude response of a filter chain for clipping analysis and for
/// drawing the response curve in the UI.
/// </summary>
public static class Biquad
{
    /// <summary>Q used when a filter line omits Q (APO defaults shelves to a gentle slope).</summary>
    public static double DefaultQ(FilterType type) => type is FilterType.LowShelf or FilterType.HighShelf ? 0.707 : 1.0;

    public static BiquadCoefficients Design(FilterType type, double sampleRate, double frequencyHz, double gainDb, double q)
    {
        // Clamp into the audible/valid range; APO ignores out-of-range the same way.
        frequencyHz = Math.Clamp(frequencyHz, 1.0, sampleRate / 2.0 - 1.0);
        q = Math.Max(q, 0.025);

        var a = Math.Pow(10.0, gainDb / 40.0);
        var w0 = 2.0 * Math.PI * frequencyHz / sampleRate;
        var cos = Math.Cos(w0);
        var sin = Math.Sin(w0);
        var alpha = sin / (2.0 * q);
        var sqrtA = Math.Sqrt(a);

        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case FilterType.Peaking:
                b0 = 1 + alpha * a;
                b1 = -2 * cos;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cos;
                a2 = 1 - alpha / a;
                break;
            case FilterType.LowShelf:
                b0 = a * ((a + 1) - (a - 1) * cos + 2 * sqrtA * alpha);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - 2 * sqrtA * alpha);
                a0 = (a + 1) + (a - 1) * cos + 2 * sqrtA * alpha;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - 2 * sqrtA * alpha;
                break;
            case FilterType.HighShelf:
                b0 = a * ((a + 1) + (a - 1) * cos + 2 * sqrtA * alpha);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - 2 * sqrtA * alpha);
                a0 = (a + 1) - (a - 1) * cos + 2 * sqrtA * alpha;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - 2 * sqrtA * alpha;
                break;
            case FilterType.LowPass:
                b0 = (1 - cos) / 2;
                b1 = 1 - cos;
                b2 = (1 - cos) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case FilterType.HighPass:
                b0 = (1 + cos) / 2;
                b1 = -(1 + cos);
                b2 = (1 + cos) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case FilterType.Notch:
                b0 = 1;
                b1 = -2 * cos;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case FilterType.BandPass:
                b0 = alpha;
                b1 = 0;
                b2 = -alpha;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case FilterType.AllPass:
                b0 = 1 - alpha;
                b1 = -2 * cos;
                b2 = 1 + alpha;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

        return new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>Magnitude response in dB at <paramref name="frequencyHz"/>.</summary>
    public static double MagnitudeDb(in BiquadCoefficients c, double sampleRate, double frequencyHz)
    {
        var w = 2.0 * Math.PI * Math.Min(frequencyHz, sampleRate / 2.0 - 1.0) / sampleRate;
        var cosW = Math.Cos(w);
        var cos2W = Math.Cos(2.0 * w);

        var numerator = c.B0 * c.B0 + c.B1 * c.B1 + c.B2 * c.B2
                        + 2.0 * (c.B0 * c.B1 + c.B1 * c.B2) * cosW
                        + 2.0 * c.B0 * c.B2 * cos2W;
        var denominator = 1.0 + c.A1 * c.A1 + c.A2 * c.A2
                          + 2.0 * (c.A1 + c.A1 * c.A2) * cosW
                          + 2.0 * c.A2 * cos2W;

        // Guard against numeric dust at extreme settings.
        numerator = Math.Max(numerator, 1e-30);
        denominator = Math.Max(denominator, 1e-30);

        return 10.0 * Math.Log10(numerator / denominator);
    }
}
