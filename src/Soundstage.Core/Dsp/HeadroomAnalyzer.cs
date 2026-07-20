using Soundstage.Core.Apo;

namespace Soundstage.Core.Dsp;

/// <summary>Result of analyzing a filter chain's worst-case gain.</summary>
public sealed record HeadroomReport(
    double PeakBoostDb,
    double PeakFrequencyHz,
    double AuthorPreampDb,
    double RecommendedPreampDb,
    double SafetyMarginDb)
{
    /// <summary>How much the analyzer had to pull the preamp below the author's value (0 when none).</summary>
    public double AutoTrimDb => Math.Max(0, AuthorPreampDb - RecommendedPreampDb);

    /// <summary>Distance from worst-case peak to 0 dBFS after the recommended preamp.</summary>
    public double HeadroomDb => -(RecommendedPreampDb + PeakBoostDb);

    /// <summary>True when the chain as authored (before auto-trim) would exceed 0 dBFS.</summary>
    public bool WouldClipWithoutTrim => AuthorPreampDb + PeakBoostDb > 0;
}

/// <summary>
/// Predicts the worst-case boost of an EQ chain by summing per-filter magnitude responses
/// over a log-spaced frequency grid, then recommends a preamp that keeps the total at or
/// below -margin dBFS. This is the fix for the classic "APO made my audio crackle" failure:
/// cumulative filter gain silently exceeding full scale.
/// </summary>
public static class HeadroomAnalyzer
{
    public const int GridPoints = 512;
    public const double MinFrequency = 20.0;
    public const double MaxFrequency = 20000.0;

    /// <summary>
    /// Analyzes a chain.
    /// </summary>
    /// <param name="authorPreampDb">The preamp the preset author asked for.</param>
    /// <param name="filters">Enabled parametric filters in the chain (preset bands + effect shelves).</param>
    /// <param name="graphicPoints">Graphic EQ points, if a graphic preset is active (piecewise-linear in log-f, like APO).</param>
    /// <param name="broadbandGainsDb">Frequency-independent worst-case gains (e.g. stereo width's anti-phase gain).</param>
    /// <param name="sampleRate">Device sample rate for biquad design.</param>
    /// <param name="safetyMarginDb">Required distance below 0 dBFS.</param>
    public static HeadroomReport Analyze(
        double authorPreampDb,
        IEnumerable<FilterCommand> filters,
        IReadOnlyList<GraphicEqPoint>? graphicPoints = null,
        IEnumerable<double>? broadbandGainsDb = null,
        double sampleRate = 48000,
        double safetyMarginDb = 0.5)
    {
        var coefficients = filters
            .Where(f => f.Enabled)
            .Select(f => Biquad.Design(f.Type, sampleRate, f.FrequencyHz, f.GainDb, f.Q ?? Biquad.DefaultQ(f.Type)))
            .ToList();

        var sortedGraphic = graphicPoints is { Count: > 0 }
            ? graphicPoints.OrderBy(p => p.FrequencyHz).ToList()
            : null;

        var peakBoost = 0.0;
        var peakFrequency = MinFrequency;
        var logMin = Math.Log10(MinFrequency);
        var logMax = Math.Log10(MaxFrequency);

        for (var i = 0; i < GridPoints; i++)
        {
            var f = Math.Pow(10, logMin + (logMax - logMin) * i / (GridPoints - 1));
            var total = 0.0;
            foreach (var c in coefficients)
            {
                total += Biquad.MagnitudeDb(c, sampleRate, f);
            }

            if (sortedGraphic is not null)
            {
                total += InterpolateGraphic(sortedGraphic, f);
            }

            if (total > peakBoost)
            {
                peakBoost = total;
                peakFrequency = f;
            }
        }

        if (broadbandGainsDb is not null)
        {
            foreach (var g in broadbandGainsDb)
            {
                if (g > 0)
                {
                    peakBoost += g;
                }
            }
        }

        // Keep authorPreamp + peakBoost <= -margin; never raise the author's preamp.
        // A chain with no positive boost cannot clip on its own — leave it untouched so a
        // flat preset stays bit-transparent instead of getting a pointless margin trim.
        var recommended = peakBoost > 1e-9
            ? Math.Min(authorPreampDb, -safetyMarginDb - peakBoost)
            : authorPreampDb;

        return new HeadroomReport(peakBoost, peakFrequency, authorPreampDb, recommended, safetyMarginDb);
    }

    /// <summary>Linear interpolation over log-frequency between graphic points; flat extension beyond the ends.</summary>
    private static double InterpolateGraphic(List<GraphicEqPoint> points, double frequency)
    {
        if (frequency <= points[0].FrequencyHz)
        {
            return points[0].GainDb;
        }

        if (frequency >= points[^1].FrequencyHz)
        {
            return points[^1].GainDb;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (frequency <= points[i].FrequencyHz)
            {
                var f0 = Math.Log10(points[i - 1].FrequencyHz);
                var f1 = Math.Log10(points[i].FrequencyHz);
                var t = (Math.Log10(frequency) - f0) / (f1 - f0);
                return points[i - 1].GainDb + t * (points[i].GainDb - points[i - 1].GainDb);
            }
        }

        return points[^1].GainDb;
    }
}
