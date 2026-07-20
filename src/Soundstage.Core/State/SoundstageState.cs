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
    public int RevertGuardSeconds { get; set; } = 10;

    public double SafetyMarginDb { get; set; } = 0.5;

    public bool LaunchOnBoot { get; set; }

    public bool StartMinimized { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Global A/B bypass hotkey.</summary>
    public string BypassHotkey { get; set; } = "Ctrl+Alt+B";

    /// <summary>Ambience is experimental and ships behind this flag.</summary>
    public bool AmbienceFeatureEnabled { get; set; }

    /// <summary>Manual override when registry detection is unavailable/wrong.</summary>
    public string? ApoConfigDirectoryOverride { get; set; }
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

        var profile = new DeviceProfile { EndpointId = endpointId, FriendlyName = friendlyName };
        Profiles.Add(profile);
        return profile;
    }
}
