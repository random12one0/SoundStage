using System.Text.Json;

using NAudio.CoreAudioApi;

using Soundstage.Shell.Audio;

namespace Soundstage.Shell.Engine;

/// <summary>
/// The bridge between the UI and the real engine. It owns the engine and the audio host, applies the
/// control messages the UI posts (power, master on/off, effect dials, EQ, output gain), and starts/stops
/// the audio path. Deliberately defensive: if the native engine can't load, the app still runs — the UI
/// works and controls simply become no-ops — so a missing DLL never takes the whole app down.
///
/// Control-message protocol (JSON posted from the web UI):
///   {"t":"power","on":true}                    start/stop routing sound through the engine
///   {"t":"master","on":true}                   engine bypass (clean pass-through when off)
///   {"t":"gain","db":-3.0}                      output trim
///   {"t":"effect","id":"bass","on":true,"v":0.6}   an effect dial: enable + 0..1 amount
///   {"t":"eq","i":3,"freq":1000,"gain":2.5,"q":1.0,"shape":0}   one EQ band
///   {"t":"eqcount","n":10}                      how many EQ bands are live
///
/// It can also notify the UI back (via the <c>notify</c> callback), e.g. "no-cable" when the CABLE
/// outlet isn't set up yet, or "running" once sound is flowing.
/// </summary>
public sealed class EngineController : IDisposable
{
    private readonly SoundstageEngine? _engine;
    private readonly EngineAudioHost? _host;
    private readonly Action<string>? _notify;
    private bool _disposed;

    public EngineController(Action<string>? notify = null)
    {
        _notify = notify;
        try
        {
            _engine = new SoundstageEngine();
            _host = new EngineAudioHost(_engine);
        }
        catch
        {
            // Native engine unavailable (e.g. DLL missing in a dev run) — degrade to UI-only.
            _engine = null;
            _host = null;
        }
    }

    public bool EngineAvailable => _engine is not null;

    /// <summary>Apply one JSON control message from the UI. Silently ignores anything malformed.</summary>
    public void HandleMessage(string json)
    {
        if (_engine is null)
        {
            return;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("t", out JsonElement typeEl))
            {
                return;
            }

            switch (typeEl.GetString())
            {
                case "power":
                    {
                        bool on = Bool(root, "on");
                        _engine.SetEnabled(on);   // master follows the power switch
                        if (on) { StartProcessing(); } else { StopProcessing(); }
                    }
                    break;
                case "master":
                    _engine.SetEnabled(Bool(root, "on"));
                    break;
                case "gain":
                    _engine.SetOutputGainDb(Num(root, "db", 0.0));
                    break;
                case "effect":
                    ApplyEffect(root);
                    break;
                case "eqcount":
                    _engine.SetEqBandCount((int)Num(root, "n", 0));
                    break;
                case "eq":
                    _engine.SetEqBand((int)Num(root, "i", 0), (BandType)(int)Num(root, "shape", 0),
                        Num(root, "freq", 1000), Num(root, "gain", 0), Num(root, "q", 1.0));
                    break;
            }
        }
    }

    // Maps a UI effect dial (0..1 amount) onto real engine parameters. First-pass tuning — the exact
    // curves are easy to adjust once we can hear them. Air/Warmth are EQ shelves (bands 1/0); Night is
    // a simple level trim for v1 (a proper night mode shares the compressor, which the Leveler owns).
    private void ApplyEffect(JsonElement root)
    {
        if (_engine is null)
        {
            return;
        }

        string id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? "" : "";
        bool on = Bool(root, "on");
        double v = Math.Clamp(Num(root, "v", 0.0), 0.0, 1.0);

        switch (id)
        {
            case "bass":
                _engine.EnableBass(on);
                _engine.SetBass(v, 90.0, 1.5 + v * 2.5);
                break;
            case "ambience":
                _engine.EnableReverb(on);
                _engine.SetReverb(0.6, 1.6, 0.5, 18.0, 0.85, v * 0.4);
                break;
            case "width":
                _engine.EnableWidth(on);
                _engine.SetWidth(1.0 + v);  // 0..1 -> 1..2 (wider); 1 = unchanged
                break;
            case "leveler":
                _engine.EnableCompressor(on);
                _engine.SetCompressor(-10.0 - v * 20.0, 1.5 + v * 3.0, 6.0, v * 4.0, 15.0, 150.0);
                break;
            case "warmth":
                _engine.EnableEq(true);
                _engine.SetEqBandCount(2);
                _engine.SetEqBand(0, BandType.LowShelf, 200.0, on ? v * 8.0 : 0.0, 0.707);
                break;
            case "air":
                _engine.EnableEq(true);
                _engine.SetEqBandCount(2);
                _engine.SetEqBand(1, BandType.HighShelf, 10000.0, on ? v * 8.0 : 0.0, 0.707);
                break;
            case "night":
                _engine.SetOutputGainDb(on ? -10.0 * v : 0.0);
                break;
        }
    }

    /// <summary>Find the "CABLE" outlet + the real speakers and start routing sound through the engine.</summary>
    public void StartProcessing()
    {
        if (_host is null || _host.IsRunning)
        {
            return;
        }

        try
        {
            MMDevice? cable = null;
            MMDevice? speakers = null;
            using (var mm = new MMDeviceEnumerator())
            {
                MMDevice defaultRender = mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                foreach (MMDevice d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    bool isCable = d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                                || d.FriendlyName.Contains("Soundstage", StringComparison.OrdinalIgnoreCase);
                    if (isCable && cable is null)
                    {
                        cable = d;
                    }
                    else if (!isCable && speakers is null)
                    {
                        speakers = d;
                    }
                }

                // Prefer the real default device for output when it isn't the CABLE itself.
                if (speakers is null ||
                    !defaultRender.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                {
                    speakers = defaultRender;
                }

                if (cable is null)
                {
                    _notify?.Invoke("no-cable");
                    return;
                }

                if (speakers is not null && !string.Equals(cable.ID, speakers.ID, StringComparison.Ordinal))
                {
                    _host.Start(cable, speakers);
                    _notify?.Invoke("running");
                }
            }
        }
        catch
        {
            _notify?.Invoke("audio-error");
        }
    }

    public void StopProcessing() => _host?.Stop();

    private static bool Bool(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement el) &&
           (el.ValueKind == JsonValueKind.True ||
            (el.ValueKind == JsonValueKind.Number && el.GetDouble() != 0.0));

    private static double Num(JsonElement root, string name, double fallback)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : fallback;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host?.Dispose();
        _engine?.Dispose();
    }
}
