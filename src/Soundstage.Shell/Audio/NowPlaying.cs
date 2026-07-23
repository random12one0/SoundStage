using Windows.Media.Control;

namespace Soundstage.Shell.Audio;

/// <summary>
/// What's actually playing, by name.
///
/// Windows keeps a list of media sessions — the same ones behind the volume-key overlay and the
/// lock screen — and any app that plays media properly registers one: Spotify, browsers, players,
/// most games' launchers. We ask for the session that's currently playing and read its title and
/// artist. It's a much better answer than "processing your sound".
///
/// Everything degrades quietly: if nothing has registered a session (a lot of games just open a
/// WASAPI stream and never tell Windows what they're playing), we fall back to the audio-session
/// process name, and failing that we say nothing rather than guess.
/// </summary>
public static class NowPlaying
{
    public sealed record Track(string Title, string Artist, string App, bool Playing);

    private static GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static bool _initFailed;

    /// <summary>The current track, or null if nothing is playing that Windows knows about.</summary>
    public static async Task<Track?> CurrentAsync()
    {
        if (_initFailed)
        {
            return null;
        }

        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager is null)
            {
                return null;
            }

            // Prefer whichever session is actually playing over whichever happens to be "current",
            // since a paused Spotify shouldn't outrank a playing browser.
            GlobalSystemMediaTransportControlsSession? best = null;
            foreach (GlobalSystemMediaTransportControlsSession session in _manager.GetSessions())
            {
                try
                {
                    var status = session.GetPlaybackInfo().PlaybackStatus;
                    if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        best = session;
                        break;
                    }

                    best ??= session;
                }
                catch
                {
                    // Sessions come and go while we're looking at them.
                }
            }

            best ??= _manager.GetCurrentSession();
            if (best is null)
            {
                return null;
            }

            GlobalSystemMediaTransportControlsSessionMediaProperties props =
                await best.TryGetMediaPropertiesAsync();

            bool playing = false;
            try
            {
                playing = best.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                // Assume not playing rather than claim it is.
            }

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            if (string.IsNullOrWhiteSpace(artist)) { artist = props?.AlbumArtist ?? ""; }

            string app = FriendlyApp(best.SourceAppUserModelId ?? "");

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(app))
            {
                return null;
            }

            return new Track(title.Trim(), artist.Trim(), app, playing);
        }
        catch
        {
            // On a machine where the media session API isn't available, stop trying.
            _initFailed = true;
            return null;
        }
    }

    /// <summary>Turn an app user-model id into something worth showing a person.</summary>
    private static string FriendlyApp(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return "";
        }

        string id = aumid;

        // Packaged apps look like "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify".
        int bang = id.IndexOf('!');
        if (bang > 0) { id = id[..bang]; }
        int underscore = id.IndexOf('_');
        if (underscore > 0) { id = id[..underscore]; }
        int dot = id.LastIndexOf('.');
        if (dot > 0 && dot < id.Length - 1) { id = id[(dot + 1)..]; }

        // Desktop apps come through as an exe name.
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { id = id[..^4]; }

        return Pretty(id);
    }

    private static string Pretty(string name) => name.ToLowerInvariant() switch
    {
        "spotify" or "spotifymusic" => "Spotify",
        "chrome" or "msedge" or "firefox" or "brave" or "opera" or "comet" => "your browser",
        "vlc" => "VLC",
        "mpc-hc64" or "mpc-be64" => "MPC",
        "netflix" => "Netflix",
        "plex" or "plexdesktop" => "Plex",
        "discord" => "Discord",
        "steam" => "Steam",
        _ => name,
    };
}
