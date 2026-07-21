namespace Soundstage.Core.Effects;

/// <summary>
/// Plans a smooth "ease to the new sound" transition for effect changes. Equalizer APO applies a
/// static config on every reload, so a big change in the night-mode shelf, the width matrix or the
/// loudness reference lands as one step and can click/pop. To avoid that we don't jump: the
/// controller writes a short series of intermediate configs that interpolate from the old settings
/// to the new ones, so each reload moves the sound only a little and the change is inaudible.
///
/// This is pure and deterministic (no timing, no I/O) so it can be unit-tested; the controller owns
/// the scheduling and the file writes. It runs for EVERY effect change regardless of who made it —
/// a slider drag, a toggle, or an automation rule — because it lives below the UI, in the controller.
///
/// Ambience is intentionally excluded from the interpolation: it is a convolution whose dry path
/// stays at full level, so toggling it adds no broadband jump to smooth. Every intermediate simply
/// carries the target's ambience setting.
/// </summary>
public static class EffectRamp
{
    /// <summary>A change smaller than this (in rough dB-equivalent) isn't worth ramping — apply it in one step.</summary>
    public const double MinRampDb = 1.5;

    /// <summary>Roughly how many dB of change each ramp step covers; drives the step count.</summary>
    private const double DbPerStep = 2.5;

    private const int MinSteps = 3;
    private const int MaxSteps = 12;

    /// <summary>True when the difference between two settings is large enough to be worth easing into.</summary>
    public static bool ShouldRamp(EffectSettings from, EffectSettings to) => Distance(from, to) > MinRampDb;

    /// <summary>
    /// How many interpolation slices to use for this change (including the final target step). More
    /// steps for bigger jumps, clamped so a tiny nudge doesn't over-ramp and a huge one doesn't spam.
    /// </summary>
    public static int StepCount(EffectSettings from, EffectSettings to) =>
        Math.Clamp((int)Math.Ceiling(Distance(from, to) / DbPerStep) + 1, MinSteps, MaxSteps);

    /// <summary>
    /// Interior interpolation at fraction <paramref name="t"/> (0..1). Enabling an effect ramps it up
    /// from zero; disabling ramps it down to zero (the caller's final committed apply then removes it).
    /// Overrides are dropped for interior steps so the value moves smoothly with intensity.
    /// </summary>
    public static EffectSettings Lerp(EffectSettings from, EffectSettings to, double t) => new(
        LerpNight(from.NightMode, to.NightMode, t),
        LerpLoudness(from.Loudness, to.Loudness, t),
        LerpWidth(from.StereoWidth, to.StereoWidth, t),
        to.Ambience,
        LerpFidelity(from.Fidelity, to.Fidelity, t));

    private static NightModeSettings LerpNight(NightModeSettings a, NightModeSettings b, double t)
    {
        if (!a.Enabled && !b.Enabled)
        {
            return b;
        }

        var ia = a.Enabled ? a.Intensity : 0;
        var ib = b.Enabled ? b.Intensity : 0;
        return new NightModeSettings(Enabled: true, Intensity: LerpInt(ia, ib, t));
    }

    private static LoudnessSettings LerpLoudness(LoudnessSettings a, LoudnessSettings b, double t)
    {
        if (!a.Enabled && !b.Enabled)
        {
            return b;
        }

        var ia = a.Enabled ? a.Intensity : 0;
        var ib = b.Enabled ? b.Intensity : 0;
        return new LoudnessSettings(Enabled: true, Intensity: LerpInt(ia, ib, t));
    }

    private static StereoWidthSettings LerpWidth(StereoWidthSettings a, StereoWidthSettings b, double t)
    {
        if (!a.Enabled && !b.Enabled)
        {
            return b;
        }

        // 0% == neutral (width factor 1.0), so a disabled endpoint interpolates as 0%.
        var wa = a.Enabled ? a.WidthPercent : 0;
        var wb = b.Enabled ? b.WidthPercent : 0;
        return new StereoWidthSettings(Enabled: true, WidthPercent: LerpInt(wa, wb, t));
    }

    private static FidelitySettings LerpFidelity(FidelitySettings a, FidelitySettings b, double t)
    {
        if (!a.Enabled && !b.Enabled)
        {
            return b;
        }

        var ia = a.Enabled ? a.Intensity : 0;
        var ib = b.Enabled ? b.Intensity : 0;
        return new FidelitySettings(Enabled: true, Intensity: LerpInt(ia, ib, t));
    }

    private static int LerpInt(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    /// <summary>
    /// A single dB-equivalent "how different do these two sounds feel" number. Dominated by the
    /// night-mode bass shelf (the loudest pop source), with loudness reference and width matrix
    /// changes folded in on comparable scales so any of them can trigger and size a ramp.
    /// </summary>
    private static double Distance(EffectSettings a, EffectSettings b) =>
        Math.Abs(NightBassDb(b) - NightBassDb(a))
        + Math.Abs(NightLevelDb(b) - NightLevelDb(a))
        + Math.Abs(LoudnessRefDb(b) - LoudnessRefDb(a)) * 0.4
        + Math.Abs(WidthDb(b) - WidthDb(a)) * 4.0
        + Math.Abs(FidelityDb(b) - FidelityDb(a));

    private static double NightBassDb(EffectSettings e) => e.NightMode.Enabled ? Math.Abs(e.NightMode.EffectiveBassCutDb) : 0;

    private static double NightLevelDb(EffectSettings e) => e.NightMode.Enabled ? e.NightMode.EffectiveLevelReductionDb : 0;

    // Disabled loudness reads as the minimal-correction reference so enabling it counts as a real change.
    private static double LoudnessRefDb(EffectSettings e) =>
        e.Loudness.Enabled ? e.Loudness.EffectiveReferenceLevel : LoudnessSettings.MinReferenceLevel;

    private static double WidthDb(EffectSettings e) => e.StereoWidth.Enabled ? e.StereoWidth.MaxGainDb : 0;

    private static double FidelityDb(EffectSettings e) => e.Fidelity.Enabled ? e.Fidelity.PresenceDb + e.Fidelity.AirDb : 0;
}
