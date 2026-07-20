namespace Soundstage.Core.Automation;

/// <summary>
/// Known process-name groups for audio-app detection and friendly display. Kept in Core so
/// the automation templates, the "now playing" readout, and rule suggestions all agree.
/// </summary>
public static class AudioApps
{
    /// <summary>Web browsers (including newer Chromium forks like Comet, Arc, Zen).</summary>
    public static readonly IReadOnlyList<string> Browsers =
    [
        "chrome", "msedge", "edge", "firefox", "brave", "opera", "operagx", "vivaldi",
        "arc", "zen", "comet", "librewolf", "waterfox", "chromium", "iexplore",
    ];

    /// <summary>Dedicated music/streaming players.</summary>
    public static readonly IReadOnlyList<string> MusicPlayers =
    [
        "spotify", "tidal", "deezer", "foobar2000", "musicbee", "itunes", "applemusic",
        "aimp", "winamp", "amazonmusic", "qobuz", "audirvana",
    ];

    /// <summary>Video/media players.</summary>
    public static readonly IReadOnlyList<string> MediaPlayers =
    [
        "vlc", "mpc-hc", "mpc-hc64", "mpv", "potplayer", "potplayermini64", "wmplayer", "nplayer",
    ];

    public static readonly IReadOnlyList<string> Games =
    [
        "steam", "valorant", "csgo", "cs2", "javaw", "gta5", "eldenring", "cyberpunk2077",
        "leagueclient", "league of legends", "overwatch", "fortniteclient",
    ];

    /// <summary>
    /// Utilities and background apps that hold audio sessions but aren't "what you're
    /// listening to" — excluded from the now-playing readout and app triggers so an RGB app
    /// or a notification chime doesn't masquerade as your music.
    /// </summary>
    public static readonly IReadOnlyList<string> Noise =
    [
        "signalrgb", "icue", "logi", "logioptionsplus", "razer synapse", "razersynapse",
        "openrgb", "nvcontainer", "nvidia", "wallpaper", "wallpaper32", "wallpaper64",
        "systemsettings", "shellexperiencehost", "textinputhost", "searchhost",
        "audiodg", "svchost", "gamebar", "gamebarft", "explorer", "dwm", "steamwebhelper",
    ];

    public static bool IsNoise(string processName)
    {
        var p = processName.ToLowerInvariant();
        return Noise.Any(n => p.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>Friendly display name for a known process (falls back to the raw name, Title Cased).</summary>
    public static string Pretty(string processName) => processName.ToLowerInvariant() switch
    {
        "spotify" => "Spotify",
        "chrome" => "Chrome",
        "msedge" or "edge" => "Edge",
        "firefox" => "Firefox",
        "brave" => "Brave",
        "opera" or "operagx" => "Opera",
        "vivaldi" => "Vivaldi",
        "arc" => "Arc",
        "zen" => "Zen",
        "comet" => "Comet",
        "vlc" => "VLC",
        "mpv" => "mpv",
        "potplayer" or "potplayermini64" => "PotPlayer",
        "tidal" => "Tidal",
        "foobar2000" => "foobar2000",
        "musicbee" => "MusicBee",
        "itunes" => "iTunes",
        "amazonmusic" => "Amazon Music",
        "steam" => "Steam",
        "discord" => "Discord",
        _ => processName.Length == 0 ? processName : char.ToUpperInvariant(processName[0]) + processName[1..],
    };
}
