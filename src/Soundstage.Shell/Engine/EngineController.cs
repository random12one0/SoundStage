using System.IO;
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
    private const int NightSlot = 33;            // night mode's bass shelf
    private const int NightHighPassSlot = 34;    // ...and the high-pass under it
    private const int TotalEqBands = 35;

    /// <summary>
    /// ISO octave centres — the standard ten-band graphic EQ, which is what consumer equalisers use
    /// and what published preset curves are written against. A third-octave version was tried and
    /// dropped: it's a measurement tool, and without a measurement it's thirty-one ways to go wrong.
    /// </summary>
    private static readonly double[] Bands10 =
    {
        31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000,
    };

    // A bell's Q for a graphic EQ is set by band spacing: neighbouring bands should sum smoothly
    // rather than ripple. Octave spacing wants roughly 1.41.
    private const double Q10 = 1.41;

    private readonly SoundstageEngine? _engine;
    private readonly ApoBridge? _bridge;
    private readonly ApoTelemetry? _telemetry;
    private readonly EngineAudioHost? _host;
    private readonly Action<string>? _notify;
    private bool _disposed;

    // The two things that both trim the output gain, kept apart so they compose instead of clobber.
    private double _volumeDb;
    private double _nightDb;

    // The device the user picked for output (null = follow the Windows default), and the one they
    // nominated as the outlet we capture from (null = find a CABLE by name).
    private string? _renderDeviceId;
    private string? _outletDeviceId;

    /// <summary>Re-open the audio path so a device or buffer change takes effect immediately.</summary>
    private void Restart()
    {
        if (_host?.IsRunning != true)
        {
            return;
        }

        StopProcessing();
        StartProcessing();
    }

    public EngineController(Action<string>? notify = null)
    {
        _notify = notify;
        try
        {
            _engine = new SoundstageEngine();
            _host = new EngineAudioHost(_engine);

            // Publish settings for the APO. Harmless when the plugin isn't installed — it just means
            // nothing ever reads the file — so this is unconditional rather than gated on a setting.
            _bridge = new ApoBridge();
            _engine.AttachBridge(_bridge);

            // Read the plugin's live meters back, so the app can show levels and now-playing even
            // when the plugin (not the app) is in the audio path.
            _telemetry = new ApoTelemetry();

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
                case "sessions":
                    SendSessions();
                    return;
                case "device":
                    _renderDeviceId = root.TryGetProperty("id", out JsonElement devEl) ? devEl.GetString() : null;
                    Restart();
                    return;
                case "outlet":
                    _outletDeviceId = root.TryGetProperty("id", out JsonElement outEl) ? outEl.GetString() : null;
                    Restart();
                    return;
                case "latency":
                    if (_host is not null)
                    {
                        _host.LatencyMs = (int)Math.Clamp(Num(root, "ms", 40), 10, 200);
                        Restart();
                    }

                    return;
                case "exclusive":
                    if (_host is not null)
                    {
                        _host.Exclusive = Bool(root, "on");
                        Restart();
                    }

                    return;
                case "startup":
                    Startup.SetRunAtLogin(Bool(root, "on"));
                    return;
                case "speakertest":
                    PlaySpeakerTest((int)Num(root, "ch", -1), Num(root, "db", 0.0));
                    return;
                case "speakersetup":
                    AudioDevices.OpenWindowsSpeakerSetup();
                    return;
                case "openfolder":
                    OpenStateFolder();
                    return;
                case "apostatus":
                    SendApoStatus();
                    return;
                case "apoinstall":
                    // The UAC prompt is asynchronous and the service restart takes a few seconds, so
                    // there is nothing useful to report back straight away. The UI re-asks.
                    ApoStatus.RunInstaller(uninstall: false,
                                           deviceMatch: root.TryGetProperty("device", out JsonElement dm)
                                               ? dm.GetString() : null);
                    return;
                case "apouninstall":
                    ApoStatus.RunInstaller(uninstall: true);
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
                        // This is an A/B bypass, not a stop button. Audio keeps flowing either way;
                        // what changes is whether you're hearing Soundstage or the original. Stopping
                        // the path would mean silence, since everything is routed through the outlet.
                        //
                        // It deliberately does NOT open the audio path — that's "engage"'s job, done
                        // once on boot. Opening it here caused an infinite loop: with no outlet (no
                        // cable, no plugin) StartProcessing replied "no-cable", the UI answered by
                        // flipping power off, which sent another "power" straight back — and every lap
                        // re-enumerated every audio device on the UI thread, freezing the window.
                        _engine.SetEnabled(Bool(root, "on"));
                    }

                    break;
                case "panic":
                    // Get out of the way. Stop processing and open the Windows sound dialog so the
                    // default device can be pointed back at real speakers. Deliberately does not try
                    // to change the default itself — when someone reaches for this, the last thing
                    // they need is the app making one more decision on their behalf.
                    StopProcessing();
                    AudioDevices.OpenWindowsSpeakerSetup();
                    _notify?.Invoke(JsonSerializer.Serialize(new { t = "panicked" }));
                    return;
                case "engage":
                    // Actually open or release the audio path. Separate from bypass on purpose.
                    if (Bool(root, "on")) { StartProcessing(); } else { StopProcessing(); }
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
                    _engine.EnableSubFeed(Bool(root, "subfeed"));
                    break;
                case "trim":
                    _engine.SetChannelTrimDb((int)Num(root, "ch", -1), Num(root, "db", 0.0));
                    break;
                case "limiter":
                    _engine.SetLimiter(Bool(root, "on"), Num(root, "ceiling", -1.0), Num(root, "release", 80.0));
                    break;
                case "bassmgmt":
                    _engine.SetBassManagement(
                        Bool(root, "on"),
                        Num(root, "crossover", 80.0),
                        (int)Num(root, "smallMask", 0xFF),
                        Num(root, "subGain", 1.0));
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
                // Crossover and drive are fine-tune parameters (the "specialties" tab). When the UI
                // sends them, use them; otherwise fall back to the values the amount dial derives, so
                // the dashboard knob alone still works exactly as before.
                _engine.SetBass(v, Num(root, "crossover", 90.0), Num(root, "drive", 1.5 + v * 2.5));
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
                ApplyNight(on, v);
                break;
        }
    }

    /// <summary>
    /// Night mode, as a receiver means it — not a volume knob.
    ///
    /// What actually wakes the house is low frequency (it goes through walls and floors where the
    /// mids and highs don't) and sudden peaks (an explosion after quiet dialogue). So this cuts the
    /// bass that travels and squashes the peaks that startle, while leaving speech alone — the point
    /// is to keep everything audible at a lower ceiling, not to make the whole film quieter.
    /// </summary>
    private void ApplyNight(bool on, double v)
    {
        if (_engine is null)
        {
            return;
        }

        // Two stages on the low end, because a shelf alone is too gentle where it matters. The shelf
        // thins the upper bass; the high-pass removes the deep rumble underneath it, which is the
        // part that actually travels through a floor.
        _engine.SetEqBand(NightSlot, BandType.LowShelf, 150.0, on ? -12.0 * v : 0.0, 0.707);
        _engine.SetEqBand(NightHighPassSlot, BandType.Highpass,
            on ? 30.0 + (v * 50.0) : 5.0,   // effectively out of the way when off
            0.0, 0.707);

        // Dynamics: bring the ceiling down, and lift the quiet parts slightly so dialogue doesn't
        // disappear along with the explosions. Modest makeup — enough to keep speech up, not enough
        // to hand back the level we just took off the bass.
        _engine.EnableNight(on);
        _engine.SetNight(
            thresholdDb: -16.0 - ((1.0 - v) * 10.0),   // more amount -> starts working sooner
            ratio: 2.0 + (v * 6.0),
            makeupDb: v * 2.5,
            attackMs: 5.0,
            releaseMs: 140.0);

        // Only a token trim: the compressor is doing the work, so this is just headroom.
        _nightDb = on ? -2.0 * v : 0.0;
        ApplyOutputGain();
    }

    // The Ambience page's parameters, kept here so the dashboard dial (which only sets the mix) and
    // the page (which sets everything else) don't overwrite each other.
    private double _rvSize = 0.6, _rvDecay = 1.8, _rvDamping = 0.4, _rvPreDelay = 22.0, _rvWidth = 0.65;
    private double _rvMixFromDial = 0.14;
    private double _rvDiffusion = 0.75, _rvLowCut = 120.0, _rvHighCut = 8000.0;
    private double _rvEarly = 0.5, _rvMod = 0.2;

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
        _rvEarly = Math.Clamp(Num(root, "early", _rvEarly), 0.0, 1.0);
        _rvMod = Math.Clamp(Num(root, "mod", _rvMod), 0.0, 1.0);

        _engine.SetReverb(_rvSize, _rvDecay, _rvDamping, _rvPreDelay, _rvWidth, mix);
        _engine.SetReverbTone(_rvDiffusion, _rvLowCut, _rvHighCut);
        _engine.SetReverbCharacter(_rvEarly, _rvMod);
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
                ApplyGraphicEq(10, Array.Empty<double>());
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

        ApplyGraphicEq(10, gains.ToArray());
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

        for (int i = 0; i < GraphicSlots; i++)
        {
            if (i < Bands10.Length)
            {
                double gain = i < gains.Length ? Math.Clamp(gains[i], -18.0, 18.0) : 0.0;
                _engine.SetEqBand(i, BandType.Peaking, Bands10[i], gain, Q10);
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

        // Plugin mode replaces this whole path, and the two must never run at once.
        //
        // With the plugin installed, our DSP already runs inside Windows' audio engine on the way to
        // the speakers. If the app ALSO opened a capture-and-replay path it would fight for the same
        // device: apps get "can't play right now", the test tone finds the device busy, and the
        // channel count we report describes a stream nobody is listening to. Every symptom of that
        // clash looks like a broken app rather than two copies of Soundstage competing.
        //
        // So when the plugin is live, the app is a control panel and nothing more.
        if (ApoStatus.AttachedDevices().Count > 0)
        {
            _notify?.Invoke(JsonSerializer.Serialize(new
            {
                t = "running",
                mode = "plugin",
                output = "Wherever Windows is playing",
                format = "Processed inside Windows",
                channels = 0,
                inChannels = 0,
            }));
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

                    // An explicitly nominated outlet wins over guessing by name.
                    if (_outletDeviceId is not null)
                    {
                        if (string.Equals(d.ID, _outletDeviceId, StringComparison.Ordinal)) { cable = d; }
                    }
                    else if (isCable && cable is null)
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
                    _notify?.Invoke(JsonSerializer.Serialize(new
                    {
                        t = "audio-error",
                        detail = speakers is null
                            ? "No speakers to play to — every device looks like an outlet."
                            : "The outlet and the output are the same device, which would feed back.",
                    }));
                    return;
                }

                _host.Start(cable, speakers);
                _notify?.Invoke(JsonSerializer.Serialize(new
                {
                    t = "running",
                    outlet = cable.FriendlyName,
                    output = speakers.FriendlyName,
                    format = _host.FormatDescription,
                    channels = _host.OutputChannels,
                    inChannels = _host.InputChannels,
                    outletChannels = OutletChannelCount(cable),
                }));
            }
        }
        catch (Exception ex)
        {
            // Say what actually went wrong. "Couldn't start audio" alone is useless when the cause
            // is one driver refusing one specific channel layout.
            _notify?.Invoke(JsonSerializer.Serialize(new
            {
                t = "audio-error",
                detail = _host?.LastError ?? ex.Message,
            }));
        }
    }

    /// <summary>
    /// How many channels the outlet is configured for. This is the ceiling on everything: if the
    /// virtual cable is set to stereo, Windows downmixes a 5.1 film before Soundstage ever sees it,
    /// and no amount of processing on our side can put the centre channel back.
    /// </summary>
    private static int OutletChannelCount(MMDevice device)
    {
        try { return device.AudioClient.MixFormat.Channels; }
        catch { return 2; }
    }

    /// <summary>Tell the UI which apps are making sound, so "when Netflix is playing" can fire.</summary>
    public void SendSessions()
    {
        try
        {
            var items = SessionWatcher.Active()
                .Select(s => new { process = s.Process, title = s.Title, active = s.Active })
                .ToList();
            _notify?.Invoke(JsonSerializer.Serialize(new { t = "sessions", items }));
        }
        catch
        {
            // Sessions are best-effort; automations simply don't fire this tick.
        }
    }

    private static bool IsCable(string name) => AudioDevices.IsCable(name);

    /// <summary>Show the user where their settings actually live.</summary>
    private static void OpenStateFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(AppState.FilePath)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Explorer unavailable — nothing worth surfacing.
        }
    }

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

    /// <summary>Per-speaker output levels for the meters drawn into the calibration faders.</summary>
    public IReadOnlyList<float> ChannelLevels =>
        _host?.OutputChannelPeaks ?? (IReadOnlyList<float>)Array.Empty<float>();

    /// <summary>
    /// Poll the plugin's live meters. Returns null when the plugin isn't actively passing audio;
    /// otherwise the per-speaker levels, overall level and channel count it is currently driving.
    /// This is how the app meters and detects playback when the plugin, not the app, holds the path.
    /// </summary>
    public (IReadOnlyList<float> Channels, float Out, int ChannelCount)? PollPluginMeters()
    {
        if (_telemetry is null || !_telemetry.Poll())
        {
            return null;
        }

        return (_telemetry.ChannelPeaks, _telemetry.OutPeak, _telemetry.Channels);
    }

    /// <summary>The layouts actually in use right now — what the content is, and what we're sending
    /// to the speakers. Not the device's channel count, which says nothing about either.</summary>
    public (int In, int Out) ActiveLayouts
        => _host is null ? (2, 2) : (_host.ActiveInputChannels, _host.ActiveOutputChannels);

    /// <summary>End-to-end delay in ms, and how hard the Leveler and limiter are working — the three
    /// numbers the UI meters want.</summary>
    public (int LatencyMs, double LevelerDb, double LimiterDb) Meters
        => (_host?.LatencyMsMeasured ?? 0,
            _engine?.GainReductionDb ?? 0.0,
            _engine?.LimiterReductionDb ?? 0.0);

    /// <summary>Send the real playback devices to the UI, so the output row and the settings picker
    /// show what's actually on this machine instead of a hard-coded name.</summary>
    /// <summary>
    /// Tell the UI whether the plugin is running, and on what. Deliberately reports three separate
    /// facts rather than one boolean: registered, attached to a device, and actually seen processing.
    /// A user whose plugin is registered but never loaded needs to know that is where it stopped.
    /// </summary>
    public void SendApoStatus()
    {
        try
        {
            var attached = ApoStatus.AttachedDevices();
            _notify?.Invoke(JsonSerializer.Serialize(new
            {
                t = "apostatus",
                registered = ApoStatus.IsRegistered(),
                devices = attached,
                active = attached.Count > 0,
                lastActivity = ApoStatus.LastActivity(),
            }));
        }
        catch
        {
            // Status is a nicety; never let it break the message loop.
        }
    }

    public void SendDeviceList()
    {
        try
        {
            var items = AudioDevices.Render().Select(d => new
            {
                id = d.Id,
                name = d.Name,
                channels = d.Channels,
                physicalChannels = d.PhysicalChannels,
                layout = d.Layout,
                physicalLayout = d.PhysicalLayout,
                isDefault = d.IsDefault,
                isCable = d.IsCable,
            }).ToList();

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
        _bridge?.Dispose();
        _telemetry?.Dispose();
    }
}
