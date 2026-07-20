namespace Soundstage.Core.Effects;

public enum EffectKind
{
    NightMode,
    Loudness,
    StereoWidth,
    Ambience,
}

/// <summary>
/// Maps a 0–100 intensity slider to an effect amount with a deliberately front-loaded
/// curve: the sweet spot lands near the middle of the slider and the top of the range is
/// intentionally over the top. Concretely, 50% already delivers ~72% of the maximum, so
/// a user who nudges the slider to the middle hears a clear, usable effect — and 100% is
/// the "nobody actually runs this" extreme.
/// </summary>
public static class IntensityCurve
{
    private const double Exponent = 0.55; // &lt;1 rises fast early: 0.5^0.55 ≈ 0.68, 0.7^0.55 ≈ 0.82

    /// <summary>Fraction 0..1 of the maximum amount for a given 0–100 intensity.</summary>
    public static double Fraction(int intensity) => Math.Pow(Math.Clamp(intensity, 0, 100) / 100.0, Exponent);

    /// <summary>Scales a maximum amount by the curve.</summary>
    public static double Scale(double maxAmount, int intensity) => maxAmount * Fraction(intensity);
}

/// <summary>
/// Night mode: a low-shelf bass cut (low frequencies are what travel through walls) plus
/// dynamic range compression via a hosted VST when configured. One intensity drives both;
/// the advanced overrides split them.
/// </summary>
public sealed record NightModeSettings(
    bool Enabled = false,
    int Intensity = 50,
    double? BassCornerOverrideHz = null,
    double? BassCutDbOverride = null,
    bool UseVstCompressor = false,
    string? VstLibraryPath = null,
    string? VstRawArguments = null)
{
    /// <summary>Bass shelf cut at 100% intensity — deep enough to essentially remove low end.</summary>
    public const double MaxBassCutDb = 24.0;

    /// <summary>Extra preamp headroom at 100% intensity when no compressor VST is available.</summary>
    public const double MaxDegradedHeadroomDb = 6.0;

    /// <summary>Corner frequency swept by intensity: gentle at low, taking out more of the bass band as it rises.</summary>
    public const double MinCornerHz = 60.0;
    public const double MaxCornerHz = 320.0;

    /// <summary>
    /// The corner the shelf pivots around. By default this rides the intensity slider (higher
    /// intensity pulls the corner up, so more of the bass band is removed); an advanced override
    /// pins it manually. Reset = clear the override and let intensity drive it again.
    /// </summary>
    public double EffectiveCornerHz => BassCornerOverrideHz ?? (MinCornerHz + (MaxCornerHz - MinCornerHz) * IntensityCurve.Fraction(Intensity));

    // 50% ≈ −16 dB (clearly quieter bass), 100% = −24 dB with the corner up at 320 Hz (bass essentially gone).
    public double EffectiveBassCutDb => BassCutDbOverride ?? -IntensityCurve.Scale(MaxBassCutDb, Intensity);

    public double EffectiveDegradedHeadroomDb => IntensityCurve.Scale(MaxDegradedHeadroomDb, Intensity);
}

/// <summary>
/// Loudness compensation via Equalizer APO's built-in volume-tracking LoudnessCorrection.
/// Intensity maps to the reference level: higher intensity → correction kicks in earlier
/// and boosts more at low listening volumes.
/// </summary>
public sealed record LoudnessSettings(
    bool Enabled = false,
    int Intensity = 50,
    double? ReferenceLevelOverride = null,
    double AttenuationDb = 0)
{
    // Higher intensity → higher reference level → correction engages across more of the
    // volume range and boosts more. Front-loaded so 50% is already a strong, usable amount
    // (≈ −64 dB reference) and 100% is the extreme end (−75 dB).
    public const double MinReferenceLevel = -45.0; // at 0% intensity — barely any correction
    public const double MaxReferenceLevel = -75.0; // at 100% — aggressive

    public double EffectiveReferenceLevel =>
        ReferenceLevelOverride ?? (MinReferenceLevel + (MaxReferenceLevel - MinReferenceLevel) * IntensityCurve.Fraction(Intensity));
}

/// <summary>
/// Mid/side stereo width applied to the front L/R pair only. 100% is untouched; below
/// narrows, above widens by boosting side (unique) relative to mid (common) content. Because
/// it only ever remixes the front left/right channels, it is safe on any layout — a 5.1/7.1
/// centre, LFE and surrounds pass through untouched. Center-panned vocals live in the mid
/// signal, so the UI marks everything past <see cref="DangerThresholdPercent"/> as the
/// thin-vocals zone.
/// </summary>
public sealed record StereoWidthSettings(
    bool Enabled = false,
    int WidthPercent = 100)
{
    public const int MinPercent = 0;
    public const int MaxPercent = 200;
    public const int DangerThresholdPercent = 140;

    public double Width => Math.Clamp(WidthPercent, MinPercent, MaxPercent) / 100.0;

    /// <summary>
    /// Worst-case broadband gain of the width matrix: in-phase (mono) content passes at
    /// unity (a+b = 1); anti-phase content sees gain w. Only widening adds gain.
    /// </summary>
    public double MaxGainDb => 20.0 * Math.Log10(Math.Max(1.0, Width));
}

/// <summary>Light convolution reverb for stereo music. Experimental, ships behind a flag.</summary>
public sealed record AmbienceSettings(
    bool Enabled = false,
    int Intensity = 40);

public sealed record EffectSettings(
    NightModeSettings NightMode,
    LoudnessSettings Loudness,
    StereoWidthSettings StereoWidth,
    AmbienceSettings Ambience)
{
    public static EffectSettings Default => new(new NightModeSettings(), new LoudnessSettings(), new StereoWidthSettings(), new AmbienceSettings());
}
