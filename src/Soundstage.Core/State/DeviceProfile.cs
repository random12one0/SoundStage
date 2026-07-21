using Soundstage.Core.Automation;
using Soundstage.Core.Effects;

namespace Soundstage.Core.State;

/// <summary>
/// Per-output-device state: which preset and effects apply to that endpoint, plus the
/// device facts needed to compile its chain section while it is offline.
/// </summary>
public sealed class DeviceProfile
{
    /// <summary>WASAPI endpoint ID, e.g. <c>{0.0.0.00000000}.{gu-id}</c>.</summary>
    public required string EndpointId { get; set; }

    public string FriendlyName { get; set; } = "";

    /// <summary>
    /// The match text emitted after <c>Device:</c> for this endpoint. Defaults to the
    /// endpoint GUID portion of <see cref="EndpointId"/>, which APO matches unambiguously.
    /// </summary>
    public string? ApoMatchSpec { get; set; }

    /// <summary>Disabled profiles get no section in the generated chain (APO passthrough).</summary>
    public bool Enabled { get; set; } = true;

    public string? ActivePresetId { get; set; }

    public EffectSettings Effects { get; set; } = EffectSettings.Default;

    /// <summary>Per-speaker level trims (attenuation) for calibrating a surround rig — e.g. turning
    /// the subwoofer down or balancing a too-loud centre. Empty = every speaker at full level.</summary>
    public List<ChannelTrim> SpeakerTrims { get; set; } = [];

    /// <summary>Which VST rack effects are enabled on this device, and at what intensity.</summary>
    public List<VstRackEntry> VstRack { get; set; } = [];

    /// <summary>This device's automation rules, evaluated top-to-bottom (last match wins).</summary>
    public List<AutomationRule> Rules { get; set; } = [];

    /// <summary>Last observed format — lets the compiler apply channel guards while the device is inactive.</summary>
    public int LastKnownChannels { get; set; } = 2;

    public int LastKnownSampleRateHz { get; set; } = 48000;

    public string EffectiveMatchSpec => ApoMatchSpec ?? ExtractEndpointGuid(EndpointId);

    public DeviceCapabilities Capabilities => new(LastKnownChannels, LastKnownSampleRateHz);

    /// <summary>
    /// WASAPI IDs look like <c>{0.0.0.00000000}.{b7de8b06-…}</c>; the trailing GUID block is
    /// the endpoint GUID Equalizer APO matches on.
    /// </summary>
    public static string ExtractEndpointGuid(string endpointId)
    {
        var lastBrace = endpointId.LastIndexOf('{');
        return lastBrace >= 0 ? endpointId[lastBrace..] : endpointId;
    }
}
