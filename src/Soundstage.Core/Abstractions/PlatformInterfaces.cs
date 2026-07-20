namespace Soundstage.Core.Abstractions;

/// <summary>Where Equalizer APO lives on this machine (registry-backed on Windows).</summary>
public interface IApoEnvironment
{
    bool IsApoInstalled { get; }

    /// <summary>The config directory (containing config.txt), or null when APO is absent.</summary>
    string? ConfigDirectory { get; }
}

/// <summary>Fixed environment for tests and for the settings override path.</summary>
public sealed class FixedApoEnvironment(string? configDirectory) : IApoEnvironment
{
    public bool IsApoInstalled => configDirectory is not null;

    public string? ConfigDirectory => configDirectory;
}

/// <summary>Live output peak metering for the headroom/clip indicator.</summary>
public interface IPeakMeter : IDisposable
{
    /// <summary>Latest peak in linear full-scale (0..1); NaN when unavailable.</summary>
    float LatestPeak { get; }

    bool IsRunning { get; }

    event Action<float>? PeakUpdated;

    /// <summary>Raised when output has been effectively at full scale for consecutive polls.</summary>
    event Action? ClippingDetected;

    void Start();

    void Stop();
}

/// <summary>Launch-on-boot registration.</summary>
public interface IStartupManager
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

/// <summary>Standard per-user data locations for Soundstage.</summary>
public static class DefaultAppPaths
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CoreInfo.ProductName);

    public static string PresetDirectory => Path.Combine(DataDirectory, "presets");

    public static string StateFilePath => Path.Combine(DataDirectory, "state.json");
}
