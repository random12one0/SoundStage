namespace Soundstage.Core.Effects;

/// <summary>
/// The bundled VST effect rack — all Airwindows (MIT-licensed), so they ship with the app. Every
/// entry's parameter array and defaults are taken from the plugin's own source, so the generated
/// <c>ChunkData</c> is exact. Each is a stereo plugin, routed to the front L/R pair (the safe scope
/// on any layout). The compiler and UI are generic over this list.
/// </summary>
public static class VstCatalog
{
    private const string Airwindows = "Airwindows (MIT)";

    public static IReadOnlyList<VstRackEffect> All { get; } =
    [
        // Virtual bass: BassKit adds low-end body + a synthesized sub. Params [Drive, Voicing,
        // BassOut, SubOut]; BassOut/SubOut are 0 at 0.5 and add level as they rise.
        new VstRackEffect(
            "bass", "Virtual Bass",
            "Adds deep bass and sub weight small speakers can't make on their own.",
            "BassKit.dll", VstRackEffect.RouteFrontStereo,
            DefaultParams: [0.5, 0.5, 0.5, 0.5],
            IntensityMaps: [new VstIntensityMap(2, 0.5, 0.9), new VstIntensityMap(3, 0.5, 0.85)],
            LicenseNote: Airwindows),

        // Warmth: PurestDrive is a subtle inter-sample drive — the cleanest one-knob "warmth".
        // Param [Drive], 0 = bypassed.
        new VstRackEffect(
            "warmth", "Warmth",
            "A gentle analog-style richness that makes everything sound fuller.",
            "PurestDrive.dll", VstRackEffect.RouteFrontStereo,
            DefaultParams: [0.0],
            IntensityMaps: [new VstIntensityMap(0, 0.0, 1.0)],
            LicenseNote: Airwindows),

        // Air/sparkle exciter: Air2 params [Hiss, Glitter, Air, Silk, Dry/Wet]; bands are 0 at 0.5.
        new VstRackEffect(
            "air", "Air",
            "Adds high-end sparkle and detail — a real exciter, not just a treble boost.",
            "Air2.dll", VstRackEffect.RouteFrontStereo,
            DefaultParams: [0.5, 0.5, 0.5, 0.0, 1.0],
            IntensityMaps: [new VstIntensityMap(2, 0.5, 0.82), new VstIntensityMap(1, 0.5, 0.68)],
            LicenseNote: Airwindows),

        // Leveler: Pressure4 is a real compressor. Params [Pressure, Speed, Mewiness, Output];
        // Pressure 0 = no gain reduction. This is the genuine dynamics APO can't do natively.
        new VstRackEffect(
            "leveler", "Leveler",
            "Evens out loud and quiet so everything sits at a comfortable, consistent level.",
            "Pressure4.dll", VstRackEffect.RouteFrontStereo,
            DefaultParams: [0.0, 0.2, 0.5, 1.0],
            IntensityMaps: [new VstIntensityMap(0, 0.0, 0.8)],
            LicenseNote: Airwindows),

        // Loudness/safety ceiling: ADClip7 params [Boost, Soften, Enhance, Mode]; Boost 0..1 = 0..18 dB
        // into a clipping ceiling. Mode stays 0 (Normal). Doubles as speaker protection.
        new VstRackEffect(
            "loud", "Loudness",
            "Pushes overall level up with a safety ceiling so it never clips or strains your speakers.",
            "ADClip7.dll", VstRackEffect.RouteFrontStereo,
            DefaultParams: [0.0, 0.5, 0.5, 0.0],
            IntensityMaps: [new VstIntensityMap(0, 0.0, 0.5)],
            LicenseNote: Airwindows),
    ];

    public static VstRackEffect? Get(string id) => All.FirstOrDefault(e => e.Id == id);
}
