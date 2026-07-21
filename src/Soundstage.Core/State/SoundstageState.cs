namespace Soundstage.Core.State;

public enum AttributionSource
{
    Manual,
    Rule,
    System,
}

/// <summary>Why the current chain looks the way it does — feeds the status readout.</summary>
public sealed record ApplyAttribution(AttributionSource Source, string Description, string? RuleId = null, string? RuleName = null)
{
    public static ApplyAttribution Manual() => new(AttributionSource.Manual, "set manually");

    public static ApplyAttribution Rule(string ruleId, string ruleName) => new(AttributionSource.Rule, $"set by rule “{ruleName}”", ruleId, ruleName);

    public static ApplyAttribution System(string description) => new(AttributionSource.System, description);
}

public sealed class AppSettings
{
    /// <summary>
    /// When on, brand-new (never-confirmed) sounds show a keep-or-revert countdown.
    /// Off by default: routine tweaking with a confirm prompt on every change is
    /// infuriating — Undo is the primary safety net instead.
    /// </summary>
    public bool ConfirmNewSounds { get; set; }

    public int RevertGuardSeconds { get; set; } = 10;

    /// <summary>
    /// Clipping protection (preamp auto-trim). When on, the headroom analyzer pulls the preamp down
    /// so a boosted EQ curve can't exceed 0 dBFS and crackle. Some users never push levels that far
    /// and would rather keep full output — turning this off leaves the preamp at the preset's own
    /// value. Effect-specific headroom (night mode / ambience) is always applied either way.
    /// </summary>
    public bool ClippingProtection { get; set; } = true;

    public double SafetyMarginDb { get; set; } = 0.5;

    /// <summary>
    /// Smoothly ramp to big effect changes instead of jumping. Equalizer APO applies a static config
    /// on every reload, so a large night-mode/width/loudness change lands in one step and can pop.
    /// With this on, the controller writes a short series of interpolated configs so the change eases
    /// in — for every source (slider, toggle, automation). On by default; off restores instant jumps.
    /// </summary>
    public bool SmoothEffectTransitions { get; set; } = true;

    public bool LaunchOnBoot { get; set; }

    public bool StartMinimized { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Show a Windows notification when an automation switches your preset or effect.</summary>
    public bool AutomationNotifications { get; set; } = true;

    /// <summary>Global A/B bypass hotkey.</summary>
    public string BypassHotkey { get; set; } = "Ctrl+Alt+B";

    /// <summary>Manual override when registry detection is unavailable/wrong.</summary>
    public string? ApoConfigDirectoryOverride { get; set; }

    /// <summary>Check GitHub for a newer release shortly after launch.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>GitHub owner/repo the updater queries (kept configurable so a repo rename is trivial).</summary>
    public string UpdateOwner { get; set; } = "random12one0";

    public string UpdateRepo { get; set; } = "unblockere1231234";
}

/// <summary>Root persisted application state.</summary>
public sealed class SoundstageState
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>A/B bypass — all Soundstage processing off while true.</summary>
    public bool BypassActive { get; set; }

    /// <summary>Master automation kill switch (rules stay defined, nothing fires).</summary>
    public bool AutomationsEnabled { get; set; } = true;

    public List<DeviceProfile> Profiles { get; set; } = [];

    /// <summary>Endpoint ID of the current default output device.</summary>
    public string? ActiveEndpointId { get; set; }

    public AppSettings Settings { get; set; } = new();

    public ApplyAttribution? LastAttribution { get; set; }

    /// <summary>SHA-256 hashes of chain configs the user has confirmed as good (guard skip list).</summary>
    public List<string> ConfirmedChainHashes { get; set; } = [];

    public DeviceProfile? ActiveProfile =>
        Profiles.FirstOrDefault(p => p.EndpointId == ActiveEndpointId);

    public DeviceProfile GetOrCreateProfile(string endpointId, string friendlyName)
    {
        var existing = Profiles.FirstOrDefault(p => p.EndpointId == endpointId);
        if (existing is not null)
        {
            if (!string.IsNullOrEmpty(friendlyName))
            {
                existing.FriendlyName = friendlyName;
            }

            return existing;
        }

        // Start with ZERO automations. The prebuilt rules are offered as Quick-start cards on the
        // Automations page instead, so nothing fires until the user opts in by adding one.
        var profile = new DeviceProfile
        {
            EndpointId = endpointId,
            FriendlyName = friendlyName,
            Rules = [],
        };
        Profiles.Add(profile);
        return profile;
    }
}
