namespace Soundstage.Core.Effects;

/// <summary>A per-speaker level trim: attenuation (dB ≤ 0) applied to one output channel.</summary>
public sealed record ChannelTrim(string Channel, double TrimDb);

/// <summary>
/// Maps an output channel count to the ordered Equalizer APO channel names and friendly labels used
/// by the speaker-calibration UI. Trims are <b>attenuation only</b>, which makes calibration safe:
/// a trim aimed at a channel the device doesn't actually have is a harmless no-op (it can never
/// misroute audio), and a cut can never clip. Surround naming varies by device (side vs. rear), so
/// a trim that lands on the "wrong" name simply does nothing — the setup wizard's test tones (later)
/// let the user confirm which control moves which speaker.
/// </summary>
public static class SpeakerLayout
{
    /// <summary>Deepest cut the UI offers per channel.</summary>
    public const double MaxCutDb = 12.0;

    public static IReadOnlyList<(string Apo, string Label)> For(int channels) => channels switch
    {
        2 => [("L", "Front Left"), ("R", "Front Right")],
        6 =>
        [
            ("L", "Front Left"), ("R", "Front Right"), ("C", "Center"), ("LFE", "Subwoofer"),
            ("RL", "Surround Left"), ("RR", "Surround Right"),
        ],
        8 =>
        [
            ("L", "Front Left"), ("R", "Front Right"), ("C", "Center"), ("LFE", "Subwoofer"),
            ("SL", "Side Left"), ("SR", "Side Right"), ("RL", "Back Left"), ("RR", "Back Right"),
        ],
        _ => [],
    };

    /// <summary>The full APO channel-name spec for a layout, e.g. <c>L R C LFE SL SR RL RR</c>.</summary>
    public static string ResetSpec(int channels) => string.Join(' ', For(channels).Select(c => c.Apo));
}
