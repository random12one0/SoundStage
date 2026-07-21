namespace Soundstage.Core.Effects;

public enum EffectKind
{
    NightMode,
    Loudness,
    StereoWidth,
    Ambience,
    Fidelity,
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
/// Night mode (native, plugin-free): a low-shelf bass cut (low frequencies are what travel
/// through walls) plus a little overall level reduction so late-night peaks feel gentler. One
/// intensity drives both; the advanced overrides split them. Nothing external sits in the audio
/// path, so night mode can never fail to load and silence your audio.
/// </summary>
public sealed record NightModeSettings(
    bool Enabled = false,
    int Intensity = 50,
    double? BassCornerOverrideHz = null,
    double? BassCutDbOverride = null)
{
    /// <summary>Bass shelf cut at 100% intensity — deep enough to essentially remove low end.</summary>
    public const double MaxBassCutDb = 24.0;

    /// <summary>Overall level reduction at 100% intensity — takes the edge off loud peaks at night.</summary>
    public const double MaxLevelReductionDb = 6.0;

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

    public double EffectiveLevelReductionDb => IntensityCurve.Scale(MaxLevelReductionDb, Intensity);
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
/// Stereo width — Boom's "Spatial" done honestly. It does NOT move sounds between speakers or
/// invent new content; it takes the left/right difference that is <i>already in the recording</i>
/// (the "side" signal) and amplifies it relative to the shared centre ("mid"), so a stereo mix
/// feels wider and more open. The slider runs 0 → 100% in one direction: 0% is untouched, 100% is
/// the widest. It is deliberately capped at a musical maximum (<see cref="MaxWidthFactor"/>) so it
/// never reaches the over-widened zone where centred vocals hollow out and hard-panned material
/// collapses toward one side — the "everything went to one speaker" problem. Because it only ever
/// rebuilds the front L/R pair, it is safe on any layout: a 5.1/7.1 centre, LFE and surrounds pass
/// through untouched.
/// </summary>
public sealed record StereoWidthSettings(
    bool Enabled = false,
    int WidthPercent = 50)
{
    public const int MinPercent = 0;
    public const int MaxPercent = 100;

    /// <summary>
    /// The widest the matrix ever goes (at 100%). Kept modest on purpose: past ~1.5× the side
    /// signal starts to dominate, thinning centred vocals and hollowing hard-panned mixes. This is
    /// the "amplify what's already there" range, not a novelty extreme.
    /// </summary>
    public const double MaxWidthFactor = 1.5;

    /// <summary>
    /// Width factor for the mid/side matrix: 1.0 (untouched) at 0% → <see cref="MaxWidthFactor"/> at
    /// 100%, front-loaded so the middle of the slider is already clearly wider.
    /// </summary>
    public double Width => 1.0 + IntensityCurve.Fraction(Math.Clamp(WidthPercent, MinPercent, MaxPercent)) * (MaxWidthFactor - 1.0);

    /// <summary>
    /// Worst-case broadband gain of the width matrix: in-phase (mono/centre) content passes at
    /// unity (a+b = 1); anti-phase content sees gain w.
    /// </summary>
    public double MaxGainDb => 20.0 * Math.Log10(Math.Max(1.0, Width));
}

/// <summary>
/// Ambience — a real convolution reverb for stereo music. At higher intensities it adds an
/// obvious sense of space, and because it is a genuine reverb tail the sound keeps ringing out for
/// a couple of seconds after the source stops (the tail is the last audio convolved with the long
/// impulse response). Intensity is the wet amount.
/// </summary>
public sealed record AmbienceSettings(
    bool Enabled = false,
    int Intensity = 50);

/// <summary>
/// Fidelity — Boom's one-knob "clarity". A fixed psychoacoustic lift that makes audio sound fuller
/// and more present: a gentle <b>presence</b> boost around 3–4 kHz (where speech and instrument
/// detail live) plus an <b>air</b> lift up top (~12 kHz) for openness and sparkle. Pure EQ, so it
/// is fully native and goes through the clipping analyzer like any other boost.
/// </summary>
public sealed record FidelitySettings(
    bool Enabled = false,
    int Intensity = 50)
{
    public const double PresenceHz = 3500.0;
    public const double PresenceQ = 0.9;
    public const double MaxPresenceDb = 5.0;

    public const double AirHz = 12000.0;
    public const double AirQ = 0.707;
    public const double MaxAirDb = 6.0;

    public double PresenceDb => IntensityCurve.Scale(MaxPresenceDb, Intensity);

    public double AirDb => IntensityCurve.Scale(MaxAirDb, Intensity);
}

public sealed record EffectSettings(
    NightModeSettings NightMode,
    LoudnessSettings Loudness,
    StereoWidthSettings StereoWidth,
    AmbienceSettings Ambience,
    FidelitySettings Fidelity)
{
    public static EffectSettings Default =>
        new(new NightModeSettings(), new LoudnessSettings(), new StereoWidthSettings(), new AmbienceSettings(), new FidelitySettings());
}
