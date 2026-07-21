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

    /// <summary>True when the process is a known web browser (so we try to read its tab title).</summary>
    public static bool IsBrowser(string processName) => Browsers.Contains(processName.ToLowerInvariant());

    /// <summary>
    /// Streaming/media services recognisable from a browser tab title, longest/most-specific
    /// token first. A browser process alone only tells us "Chrome"; the tab title tells us
    /// "YouTube" or "Netflix", which is what the user actually cares about.
    /// </summary>
    private static readonly (string Token, string Label)[] StreamingServices =
    [
        ("youtube music", "YouTube Music"),
        ("youtube", "YouTube"),
        ("netflix", "Netflix"),
        ("twitch", "Twitch"),
        ("disney+", "Disney+"),
        ("disneyplus", "Disney+"),
        ("hulu", "Hulu"),
        ("hbo max", "Max"),
        ("prime video", "Prime Video"),
        ("crunchyroll", "Crunchyroll"),
        ("apple tv", "Apple TV"),
        ("apple music", "Apple Music"),
        ("spotify", "Spotify"),
        ("soundcloud", "SoundCloud"),
        ("bandcamp", "Bandcamp"),
        ("pandora", "Pandora"),
        ("tidal", "Tidal"),
        ("deezer", "Deezer"),
        ("plex", "Plex"),
        ("vimeo", "Vimeo"),
        ("dailymotion", "Dailymotion"),
    ];

    /// <summary>
    /// The streaming service named in a browser window/tab title, or null. Case-insensitive
    /// substring match — a Chrome title like "Song - YouTube - Google Chrome" resolves to
    /// "YouTube". Deliberately avoids over-generic tokens (e.g. bare "max"/"amazon").
    /// </summary>
    public static string? DetectStreamingService(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return null;
        }

        var title = windowTitle.ToLowerInvariant();
        foreach (var (token, label) in StreamingServices)
        {
            if (title.Contains(token, StringComparison.Ordinal))
            {
                return label;
            }
        }

        return null;
    }

    /// <summary>Friendly display name for a known process (falls back to the raw name, Title Cased).</summary>
    public static string Pretty(string processName) => processName.ToLowerInvariant() switch
    {
        "spotify" => "Spotify",
        "chrome" => "Chrome",
        "chromium" => "Chromium",
        "msedge" or "edge" => "Edge",
        "firefox" => "Firefox",
        "librewolf" => "LibreWolf",
        "waterfox" => "Waterfox",
        "brave" => "Brave",
        "opera" => "Opera",
        "operagx" => "Opera GX",
        "vivaldi" => "Vivaldi",
        "arc" => "Arc",
        "zen" => "Zen",
        "comet" => "Comet",
        "iexplore" => "Internet Explorer",
        "vlc" => "VLC",
        "mpv" => "mpv",
        "mpc-hc" or "mpc-hc64" => "MPC-HC",
        "potplayer" or "potplayermini64" => "PotPlayer",
        "wmplayer" => "Windows Media Player",
        "nplayer" => "nPlayer",
        "tidal" => "Tidal",
        "deezer" => "Deezer",
        "foobar2000" => "foobar2000",
        "musicbee" => "MusicBee",
        "itunes" => "iTunes",
        "applemusic" => "Apple Music",
        "amazonmusic" => "Amazon Music",
        "qobuz" => "Qobuz",
        "audirvana" => "Audirvana",
        "aimp" => "AIMP",
        "winamp" => "Winamp",
        "steam" => "Steam",
        "discord" => "Discord",
        _ => processName.Length == 0 ? processName : char.ToUpperInvariant(processName[0]) + processName[1..],
    };
}
