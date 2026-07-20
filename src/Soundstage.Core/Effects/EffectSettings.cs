namespace Soundstage.Core.Effects;

public enum EffectKind
{
    NightMode,
    Loudness,
    StereoWidth,
    Ambience,
}

/// <summary>
/// Night mode: a low-shelf bass cut (low frequencies are what travel through walls) plus
/// dynamic range compression via a hosted VST when configured. One intensity drives both;
/// the advanced overrides split them.
/// </summary>
public sealed record NightModeSettings(
    bool Enabled = false,
    int Intensity = 50,
    double BassCornerHz = 90,
    double? BassCutDbOverride = null,
    bool UseVstCompressor = false,
    string? VstLibraryPath = null,
    string? VstRawArguments = null)
{
    /// <summary>Bass shelf cut at 100% intensity.</summary>
    public const double MaxBassCutDb = 9.0;

    /// <summary>Extra preamp headroom at 100% intensity when no compressor VST is available.</summary>
    public const double MaxDegradedHeadroomDb = 4.0;

    public double EffectiveBassCutDb => BassCutDbOverride ?? -(MaxBassCutDb * Math.Clamp(Intensity, 0, 100) / 100.0);
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
    public double EffectiveReferenceLevel => ReferenceLevelOverride ?? (-14.0 - 16.0 * Math.Clamp(Intensity, 0, 100) / 100.0);
}

/// <summary>
/// Mid/side stereo width. 100% is untouched; below narrows, above widens by attenuating
/// mid relative to side. Center-panned vocals live in the mid signal, so the UI marks
/// everything past <see cref="DangerThresholdPercent"/> as the thin-vocals zone.
/// </summary>
public sealed record StereoWidthSettings(
    bool Enabled = false,
    int WidthPercent = 100)
{
    public const int MinPercent = 0;
    public const int MaxPercent = 200;
    public const int DangerThresholdPercent = 120;

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
    int Intensity = 30);

public sealed record EffectSettings(
    NightModeSettings NightMode,
    LoudnessSettings Loudness,
    StereoWidthSettings StereoWidth,
    AmbienceSettings Ambience)
{
    public static EffectSettings Default => new(new NightModeSettings(), new LoudnessSettings(), new StereoWidthSettings(), new AmbienceSettings());
}
