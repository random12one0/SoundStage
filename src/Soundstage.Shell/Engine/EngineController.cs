using System.Text.Json;

using NAudio.CoreAudioApi;

using Soundstage.Shell.Audio;

namespace Soundstage.Shell.Engine;

/// <summary>
/// The bridge between the UI and the real engine. It owns the engine and the audio host, applies the
/// control messages the UI posts, and starts/stops the audio path. Deliberately defensive: if the
/// native engine can't load, the app still runs — the UI works and controls become no-ops — so a
/// missing DLL never takes the whole app down.
///
/// Control-message protocol (JSON posted from the web UI). Every value the UI can move has a message:
///   {"t":"power","on":true}                          start/stop routing sound through the engine
///   {"t":"master","on":true}                         engine bypass (clean pass-through when off)
///   {"t":"volume","v":0.68}                          master volume, 0..1
///   {"t":"effect","id":"bass","on":true,"v":0.6}     an effect dial: enable + 0..1 amount
///   {"t":"eq","mode":10,"gains":[...],"on":true}     graphic EQ: dB per band, 10- or 31-band
///   {"t":"reverb","size":..,"decay":..,...}          the Ambience page's full reverb parameter set
///   {"t":"upmix","on":true,"amount":0.7,...}         stereo -> surround fill
///   {"t":"trim","ch":3,"db":-2.0}                    one speaker calibration fader
///   {"t":"device","id":"..."} / {"t":"devices"}      pick / re-enumerate the output device
///   {"t":"save","state":{...}}                       persist the whole UI state to disk
///
/// It notifies the UI back through <c>notify</c> — status strings and JSON payloads (device list,
/// restored state) the page applies on load.
/// </summary>
public sealed class EngineController : IDisposable
{
    // ---- EQ slot layout ----------------------------------------------------------------------
    // The engine runs one biquad cascade, so the graphic EQ and the tone dials have to live in
    // different slots or they overwrite each other (Air used to wipe out band 1 of the user's EQ).
    // Fixed layout: 0..30 are the graphic bands, then the two tone shelves above them.
    private const int GraphicSlots = 31;
    private const int WarmthSlot = 31;
    private const int AirSlot = 32;
    private const int TotalEqBands = 33;

    /// <summary>ISO octave centres — the 10-band graphic EQ.</summary>
    private static readonly double[] Bands10 =
    {
        31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000,
    };

    /// <summary>ISO third-octave centres — the 31-band graphic EQ.</summary>
    private static readonly double[] Bands31 =
    {
        20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630,
        800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000,
    };

    // A bell's Q for a graphic EQ is set by band spacing: neighbouring bands should sum smoothly
    // rather than ripple. Octave spacing -> ~1.41, third-octave -> ~4.32.
    private const double Q10 = 1.41;
    private const double Q31 = 4.32;

    private readonly SoundstageEngine? _engine;
    private readonly EngineAudioHost? _host;
    private readonly Action<string>? _notify;
    private bool _disposed;

    // The two things that both trim the output gain, kept apart so they compose instead of clobber.
    private double _volumeDb;
    private double _nightDb;

    // The device the user picked for output (null = follow the Windows default).
    private string? _renderDeviceId;

    public EngineController(Action<string>? notify = null)
    {
        _notify = notify;
        try
        {
            _engine = new SoundstageEngine();
            _host = new EngineAudioHost(_engine);
            _engine.EnableEq(true);              // the cascade always runs; flat bands are an identity
            _engine.SetEqBandCount(TotalEqBands);
            ApplyGraphicEq(10, Array.Empty<double>());   // start flat
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

            string type = typeEl.GetString() ?? "";

            // These work with or without the native engine — they're app state, not DSP.
            switch (type)
            {
                case "save":
                    if (root.TryGetProperty("state", out JsonElement stateEl))
                    {
                        AppState.Save(stateEl.GetRawText());
                    }

                    return;
                case "devices":
                    SendDeviceList();
                    return;
                case "device":
                    _renderDeviceId = root.TryGetProperty("id", out JsonElement devEl) ? devEl.GetString() : null;
                    if (_host?.IsRunning == true)
                    {
                        StopProcessing();
                        StartProcessing();
                    }

                    return;
                case "startup":
                    Startup.SetRunAtLogin(Bool(root, "on"));
                    return;
                case "speakertest":
                    PlaySpeakerTest((int)Num(root, "ch", -1), Num(root, "db", 0.0));
                    return;
            }

            if (_engine is null)
            {
                return;
            }

            switch (type)
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
                case "volume":
                    _volumeDb = VolumeToDb(Num(root, "v", 1.0));
                    ApplyOutputGain();
                    break;
                case "effect":
                    ApplyEffect(root);
                    break;
                case "eq":
                    ApplyEq(root);
                    break;
                case "reverb":
                    ApplyReverb(root);
                    break;
                case "upmix":
                    _engine.EnableUpmix(Bool(root, "on"));
                    _engine.SetUpmix(Num(root, "amount", 0.7), Num(root, "center", 1.0), Num(root, "lfe", 1.0));
                    break;
                case "trim":
                    _engine.SetChannelTrimDb((int)Num(root, "ch", -1), Num(root, "db", 0.0));
                    break;
            }
        }
    }

    // ---- effects ----------------------------------------------------------------------------

    /// <summary>
    /// Maps a UI effect dial (0..1 amount) onto real engine parameters. First-pass tuning — these are
    /// the curves to adjust once the sound is judged by ear. Air/Warmth are tone shelves in their own
    /// EQ slots (above the graphic bands); Night is a level trim that composes with the volume dial.
    /// </summary>
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
                // The dial is the mix; the Ambience page owns the character of the reverb.
                _engine.EnableReverb(on);
                _engine.SetReverb(_rvSize, _rvDecay, _rvDamping, _rvPreDelay, _rvWidth, v * 0.4);
                _rvMixFromDial = v * 0.4;
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
                _engine.EnableEq(true);   // the tone shelves live in the EQ cascade — it must be live
                _engine.SetEqBand(WarmthSlot, BandType.LowShelf, 200.0, on ? v * 8.0 : 0.0, 0.707);
                break;
            case "air":
                _engine.EnableEq(true);
                _engine.SetEqBand(AirSlot, BandType.HighShelf, 10000.0, on ? v * 8.0 : 0.0, 0.707);
                break;
            case "night":
                _nightDb = on ? -12.0 * v : 0.0;
                ApplyOutputGain();
                break;
        }
    }

    // The Ambience page's parameters, kept here so the dashboard dial (which only sets the mix) and
    // the page (which sets everything else) don't overwrite each other.
    private double _rvSize = 0.6, _rvDecay = 1.8, _rvDamping = 0.4, _rvPreDelay = 22.0, _rvWidth = 0.65;
    private double _rvMixFromDial = 0.14;
    private double _rvDiffusion = 0.75, _rvLowCut = 120.0, _rvHighCut = 8000.0;

    private void ApplyReverb(JsonElement root)
    {
        if (_engine is null)
        {
            return;
        }

        _rvSize = Math.Clamp(Num(root, "size", _rvSize), 0.05, 1.0);
        _rvDecay = Math.Clamp(Num(root, "decay", _rvDecay), 0.1, 12.0);
        _rvDamping = Math.Clamp(Num(root, "damping", _rvDamping), 0.0, 1.0);
        _rvPreDelay = Math.Clamp(Num(root, "predelay", _rvPreDelay), 0.0, 200.0);
        _rvWidth = Math.Clamp(Num(root, "width", _rvWidth), 0.0, 1.0);
        double mix = root.TryGetProperty("mix", out _) ? Math.Clamp(Num(root, "mix", 0.0), 0.0, 1.0) : _rvMixFromDial;
        _rvMixFromDial = mix;

        if (root.TryGetProperty("on", out _))
        {
            _engine.EnableReverb(Bool(root, "on"));
        }

        _rvDiffusion = Math.Clamp(Num(root, "diffusion", _rvDiffusion), 0.0, 1.0);
        _rvLowCut = Math.Clamp(Num(root, "lowcut", _rvLowCut), 20.0, 1000.0);
        _rvHighCut = Math.Clamp(Num(root, "highcut", _rvHighCut), 1000.0, 20000.0);

        _engine.SetReverb(_rvSize, _rvDecay, _rvDamping, _rvPreDelay, _rvWidth, mix);
        _engine.SetReverbTone(_rvDiffusion, _rvLowCut, _rvHighCut);
    }

    // ---- EQ ---------------------------------------------------------------------------------

    private void ApplyEq(JsonElement root)
    {
        if (_engine is null)
        {
            return;
        }

        if (root.TryGetProperty("on", out _))
        {
            // The cascade also carries the tone shelves, so "EQ off" flattens the graphic bands
            // rather than bypassing the whole section.
            if (!Bool(root, "on"))
            {
                ApplyGraphicEq((int)Num(root, "mode", 10), Array.Empty<double>());
                return;
            }
        }

        var gains = new List<double>();
        if (root.TryGetProperty("gains", out JsonElement gainsEl) && gainsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement g in gainsEl.EnumerateArray())
            {
                gains.Add(g.ValueKind == JsonValueKind.Number ? g.GetDouble() : 0.0);
            }
        }

        ApplyGraphicEq((int)Num(root, "mode", gains.Count == 31 ? 31 : 10), gains.ToArray());
    }

    /// <summary>
    /// Push the graphic EQ into slots 0..30. Bands the current mode doesn't use are made flat rather
    /// than dropped, so switching 31 -> 10 can't leave a stale third-octave band ringing underneath.
    /// </summary>
    private void ApplyGraphicEq(int mode, double[] gains)
    {
        if (_engine is null)
        {
            return;
        }

        _engine.EnableEq(true);
        _engine.SetEqBandCount(TotalEqBands);

        double[] freqs = mode == 31 ? Bands31 : Bands10;
        double q = mode == 31 ? Q31 : Q10;

        for (int i = 0; i < GraphicSlots; i++)
        {
            if (i < freqs.Length)
            {
                double gain = i < gains.Length ? Math.Clamp(gains[i], -18.0, 18.0) : 0.0;
                _engine.SetEqBand(i, BandType.Peaking, freqs[i], gain, q);
            }
            else
            {
                _engine.SetEqBand(i, BandType.Peaking, 1000.0, 0.0, 1.0);  // identity
            }
        }
    }

    // ---- output gain ------------------------------------------------------------------------

    /// <summary>Volume 0..1 to dB. Square-law: -6 dB at half, silence at zero — what a volume slider
    /// should feel like, rather than the raw linear amplitude.</summary>
    private static double VolumeToDb(double v)
    {
        v = Math.Clamp(v, 0.0, 1.0);
        return v <= 0.0001 ? -100.0 : 40.0 * Math.Log10(v);
    }

    private void ApplyOutputGain() => _engine?.SetOutputGainDb(_volumeDb + _nightDb);

    // ---- audio path -------------------------------------------------------------------------

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
                    bool isCable = IsCable(d.FriendlyName);
                    if (isCable && cable is null)
                    {
                        cable = d;
                    }

                    // An explicitly chosen output wins over anything we'd guess.
                    if (!isCable && _renderDeviceId is not null &&
                        string.Equals(d.ID, _renderDeviceId, StringComparison.Ordinal))
                    {
                        speakers = d;
                    }
                }

                // No explicit choice: follow the Windows default, unless the default *is* the CABLE
                // (which it will be once the user routes their system through us).
                if (speakers is null && !IsCable(defaultRender.FriendlyName))
                {
                    speakers = defaultRender;
                }

                if (speakers is null)
                {
                    foreach (MMDevice d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        if (!IsCable(d.FriendlyName)) { speakers = d; break; }
                    }
                }

                if (cable is null)
                {
                    _notify?.Invoke("no-cable");
                    return;
                }

                if (speakers is null || string.Equals(cable.ID, speakers.ID, StringComparison.Ordinal))
                {
                    _notify?.Invoke("audio-error");
                    return;
                }

                _host.Start(cable, speakers);
                _notify?.Invoke("running");
            }
        }
        catch
        {
            _notify?.Invoke("audio-error");
        }
    }

    private static bool IsCable(string name)
        => name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Soundstage", StringComparison.OrdinalIgnoreCase);

    public void StopProcessing() => _host?.Stop();

    private readonly SpeakerTest _speakerTest = new();

    /// <summary>Play a calibration burst out of one speaker on the current output device.</summary>
    private void PlaySpeakerTest(int channel, double trimDb)
    {
        try
        {
            using var mm = new MMDeviceEnumerator();
            MMDevice? target = null;

            if (_renderDeviceId is not null)
            {
                foreach (MMDevice d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (string.Equals(d.ID, _renderDeviceId, StringComparison.Ordinal)) { target = d; break; }
                }
            }

            if (target is null)
            {
                MMDevice def = mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (!IsCable(def.FriendlyName))
                {
                    target = def;
                }
                else
                {
                    foreach (MMDevice d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        if (!IsCable(d.FriendlyName)) { target = d; break; }
                    }
                }
            }

            if (target is not null && !_speakerTest.Play(target, channel, trimDb))
            {
                // The device has no such speaker — tell the UI rather than flashing a button at nothing.
                _notify?.Invoke("no-speaker");
            }
        }
        catch
        {
            _notify?.Invoke("audio-error");
        }
    }

    public bool IsRunning => _host?.IsRunning == true;

    /// <summary>Current input/output peaks (0..1) for the UI meter.</summary>
    public (float In, float Out) Levels => _host is null ? (0f, 0f) : (_host.InputPeak, _host.OutputPeak);

    /// <summary>Send the real playback devices to the UI, so the output row and the settings picker
    /// show what's actually on this machine instead of a hard-coded name.</summary>
    public void SendDeviceList()
    {
        try
        {
            var items = new List<object>();
            string defaultId = "";
            using (var mm = new MMDeviceEnumerator())
            {
                try { defaultId = mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
                catch { /* no default device */ }

                foreach (MMDevice d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    int channels = 2;
                    try { channels = d.AudioClient.MixFormat.Channels; } catch { /* keep the default */ }
                    items.Add(new
                    {
                        id = d.ID,
                        name = d.FriendlyName,
                        channels,
                        isDefault = string.Equals(d.ID, defaultId, StringComparison.Ordinal),
                        isCable = IsCable(d.FriendlyName),
                    });
                }
            }

            _notify?.Invoke(JsonSerializer.Serialize(new { t = "devices", items, selected = _renderDeviceId }));
        }
        catch
        {
            // Device enumeration can fail while endpoints are changing — the UI keeps what it has.
        }
    }

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
        _speakerTest.Dispose();
        _host?.Dispose();
        _engine?.Dispose();
    }
}
