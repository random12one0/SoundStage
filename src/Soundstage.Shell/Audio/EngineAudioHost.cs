using System.Runtime.InteropServices;

using NAudio.CoreAudioApi;
using NAudio.Wave;

using Soundstage.Shell.Engine;

namespace Soundstage.Shell.Audio;

/// <summary>
/// The processing core of the Soundstage output device: it captures the audio playing on one device,
/// runs every buffer through the engine, and renders the result to another (your real speakers).
///
/// Until the Soundstage virtual device exists, point capture at any device and render to a *different*
/// one (the two must differ, or the output would feed back into the capture). Once the driver ships,
/// capture = the "Soundstage" device your apps play into, render = your speakers — seamless and
/// system-wide.
///
/// v1 processes in stereo end-to-end; surround upmix to a multichannel render device comes next.
/// This runs only on Windows (WASAPI); it is built here but tested on the user's machine.
/// </summary>
public sealed class EngineAudioHost : IDisposable
{
    private readonly SoundstageEngine _engine;
    private readonly object _sync = new();

    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;

    // Reused across callbacks so the audio thread doesn't allocate every buffer.
    private float[] _inScratch = Array.Empty<float>();
    private float[] _outScratch = Array.Empty<float>();
    private byte[] _outBytes = Array.Empty<byte>();

    // The render device's channel count (2, 6 or 8) — what the engine is asked to write per frame.
    private int _outChannels = 2;

    /// <summary>
    /// Render buffer in ms. Lower is tighter lip-sync, higher survives a busy machine without
    /// crackling. Takes effect the next time the audio path starts.
    /// </summary>
    public int LatencyMs { get; set; } = 40;

    /// <summary>Channels currently being rendered, for the UI.</summary>
    public int OutputChannels => _outChannels;

    /// <summary>Channels the outlet hands us — the container size, not what's in it.</summary>
    public int InputChannels => _inChannels;

    private int _inChannels = 2;

    // ---- what is ACTUALLY playing --------------------------------------------------------------
    // The outlet's channel count says nothing about the content: with the cable configured as 5.1,
    // Spotify's stereo still arrives as six channels, four of them silent. So we watch each channel
    // for real signal and report the layout that is genuinely in use. A hold time keeps a quiet
    // passage in a film from momentarily collapsing the reading to stereo.
    private const float ActiveFloor = 0.0004f;   // ≈ -68 dBFS, below anything audible
    private const long HoldMs = 4000;

    private readonly long[] _inLastActive = new long[8];
    private readonly long[] _outLastActive = new long[8];

    /// <summary>The layout actually arriving (2, 6 or 8) — stereo content reads as 2 even on a 5.1 outlet.</summary>
    public int ActiveInputChannels => ActiveLayout(_inLastActive, _inChannels);

    /// <summary>The layout actually leaving for the speakers, after everything we do to it.</summary>
    public int ActiveOutputChannels => ActiveLayout(_outLastActive, _outChannels);

    private static int ActiveLayout(long[] lastActive, int available)
    {
        long now = Environment.TickCount64;
        bool Live(int c) => c < available && (now - lastActive[c]) < HoldMs;

        if (Live(6) || Live(7)) { return 8; }
        if (Live(2) || Live(3) || Live(4) || Live(5)) { return 6; }
        return 2;
    }

    /// <summary>
    /// Take the output device exclusively. This is how we drive all of a receiver's speakers even
    /// when Windows' "Configure Speakers" has reverted to stereo — but nothing else can play through
    /// that device while we hold it. Takes effect on the next start.
    /// </summary>
    public bool Exclusive { get; set; }

    private WaveFormat? _outFormat;
    private bool _outIsFloat = true;

    /// <summary>Why the audio path last refused to open, for the UI to show instead of a shrug.</summary>
    public string? LastError { get; private set; }

    /// <summary>A short description of what we actually opened, for the UI to show.</summary>
    public string FormatDescription => _outFormat is null
        ? "—"
        : $"{_outFormat.Channels}ch · {_outFormat.SampleRate / 1000.0:0.#} kHz · " +
          (_outIsFloat ? "32-bit float" : $"{_outFormat.BitsPerSample}-bit");

    /// <summary>
    /// Find a format the device will actually accept, widest layout first. Shared mode only ever has
    /// one answer (the mix format), so this really matters for exclusive, where drivers are picky —
    /// NVIDIA's HDMI endpoint, for instance, takes 16-bit only.
    /// </summary>
    private WaveFormat NegotiateFormat(MMDevice device, int rate, AudioClientShareMode share)
    {
        if (share == AudioClientShareMode.Shared)
        {
            // Use the endpoint's own mix format verbatim. Anything else — even the same channel
            // count expressed as plain IEEE_FLOAT rather than EXTENSIBLE — can be rejected outright
            // for multichannel, which is exactly how a 6-channel receiver ended up refusing to open.
            try
            {
                WaveFormat mix = device.AudioClient.MixFormat;
                _outIsFloat = true;   // the Windows shared mix format is always 32-bit float
                return mix;
            }
            catch
            {
                _outIsFloat = true;
                return WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            }
        }

        // Exclusive: ask for the speakers the hardware claims, then fall back gracefully.
        int physical = 2;
        try
        {
            var info = AudioDevices.Render().FirstOrDefault(d => d.Id == device.ID);
            if (info is not null) { physical = Math.Max(info.PhysicalChannels, info.Channels); }
        }
        catch
        {
            // Fall through to stereo.
        }

        int[] layouts = physical >= 8 ? new[] { 8, 6, 2 } : physical >= 6 ? new[] { 6, 2 } : new[] { 2 };
        int[] rates = rate == 48000 ? new[] { 48000, 44100 } : new[] { rate, 48000, 44100 };

        foreach (int ch in layouts)
        {
            foreach (int r in rates)
            {
                foreach ((WaveFormat candidate, bool isFloat) in Candidates(r, ch))
                {
                    try
                    {
                        if (device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, candidate))
                        {
                            _outIsFloat = isFloat;
                            return candidate;
                        }
                    }
                    catch
                    {
                        // Some drivers throw rather than returning false; treat as unsupported.
                    }
                }
            }
        }

        _outIsFloat = true;
        return WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
    }

    /// <summary>Formats to try, best first. The bool says whether samples are float or integer PCM —
    /// WaveFormatExtensible reports its encoding as Extensible either way, so we can't ask it later.</summary>
    private static IEnumerable<(WaveFormat Format, bool IsFloat)> Candidates(int rate, int channels)
    {
        yield return (WaveFormat.CreateIeeeFloatWaveFormat(rate, channels), true);
        yield return (new WaveFormatExtensible(rate, 32, channels), false);
        yield return (new WaveFormatExtensible(rate, 24, channels), false);
        yield return (new WaveFormatExtensible(rate, 16, channels), false);
    }

    // Peak levels for the UI meter, written on the audio thread and read on the UI thread. Plain
    // fields: a torn read of a float meter is harmless, and a lock on the audio path is not.
    private volatile float _inPeak;
    private volatile float _outPeak;

    /// <summary>Loudest input sample since the last block, 0..1 — what arrived from the outlet.</summary>
    public float InputPeak => _inPeak;

    /// <summary>Loudest output sample since the last block, 0..1 — what went to the speakers.</summary>
    public float OutputPeak => _outPeak;

    private bool _running;
    private bool _disposed;

    public EngineAudioHost(SoundstageEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public bool IsRunning
    {
        get { lock (_sync) { return _running; } }
    }

    /// <summary>The default render device — a convenient render target for testing.</summary>
    public static MMDevice DefaultRenderDevice()
    {
        using var mm = new MMDeviceEnumerator();
        return mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>All active render (playback) devices, for the UI to choose capture/render endpoints.</summary>
    public static IReadOnlyList<MMDevice> RenderDevices()
    {
        using var mm = new MMDeviceEnumerator();
        var list = new List<MMDevice>();
        foreach (var d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            list.Add(d);
        }

        return list;
    }

    /// <summary>
    /// Start processing: capture what plays on <paramref name="captureDevice"/>, run it through the
    /// engine, and render to <paramref name="renderDevice"/>. The two must be different devices.
    /// </summary>
    public void Start(MMDevice captureDevice, MMDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(captureDevice);
        ArgumentNullException.ThrowIfNull(renderDevice);
        if (string.Equals(captureDevice.ID, renderDevice.ID, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Capture and render must be different devices, or the output would feed straight back " +
                "into the capture. Once the Soundstage virtual device is installed, capture from it and " +
                "render to your speakers.");
        }

        lock (_sync)
        {
            if (_running)
            {
                return;
            }

            LastError = null;
            _capture = new WasapiLoopbackCapture(captureDevice);
            int rate = _capture.WaveFormat.SampleRate;
            _engine.Prepare(rate);

            // Pick the output format. Shared mode has to take whatever Windows' speaker config says,
            // which is why a 5.1 receiver left on the stereo default plays as stereo for every app on
            // the system. Exclusive mode negotiates directly with the driver, so we can drive all the
            // speakers the hardware actually has no matter what that setting happens to be.
            AudioClientShareMode share = Exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
            WaveFormat outFormat = NegotiateFormat(renderDevice, rate, share);
            _outChannels = outFormat.Channels;
            _outFormat = outFormat;

            _buffer = new BufferedWaveProvider(outFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(200),
                DiscardOnBufferOverflow = true,
            };

            _output = new WasapiOut(renderDevice, share, useEventSync: true, latency: LatencyMs);
            try
            {
                _output.Init(_buffer);
            }
            catch (Exception ex)
            {
                // Say which device and format failed — "couldn't start audio" on its own is useless
                // when the cause is a driver refusing one specific layout.
                LastError = $"{renderDevice.FriendlyName} refused {FormatDescription}" +
                            (Exclusive ? " in exclusive mode" : "") + $" — {ex.Message}";
                try { _output.Dispose(); } catch { /* ignore */ }
                _output = null;
                try { _capture.Dispose(); } catch { /* ignore */ }
                _capture = null;
                throw;
            }

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnRecordingStopped;

            _capture.StartRecording();
            _output.Play();
            _running = true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            try { _capture?.StopRecording(); } catch { /* tearing down */ }
            try { _output?.Stop(); } catch { /* tearing down */ }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        WasapiLoopbackCapture? cap = _capture;
        BufferedWaveProvider? buffer = _buffer;
        if (cap is null || buffer is null || e.BytesRecorded <= 0)
        {
            return;
        }

        // The loopback mix format is 32-bit float; bail cleanly on anything unexpected.
        if (cap.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || cap.WaveFormat.BitsPerSample != 32)
        {
            return;
        }

        int channels = cap.WaveFormat.Channels;
        if (channels < 1)
        {
            return;
        }

        int frames = e.BytesRecorded / (4 * channels);
        if (frames <= 0)
        {
            return;
        }

        EnsureScratch(frames);

        ReadOnlySpan<float> src = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * channels * 4));
        int outCh = _outChannels;
        int capCh = Math.Min(channels, 8);

        // Measure every channel first. Which path we take has to depend on what is actually PLAYING,
        // not on how many channels the outlet happens to be configured for: with the cable set to
        // 5.1, Spotify still arrives as six channels with four of them silent, and treating that as
        // a surround source would mean the upmix never ran for ordinary music.
        float inPeak = 0f;
        Span<float> chPeak = stackalloc float[8];
        for (int n = 0; n < frames; n++)
        {
            for (int c = 0; c < capCh; c++)
            {
                float a = Math.Abs(src[(n * channels) + c]);
                if (a > inPeak) { inPeak = a; }
                if (a > chPeak[c]) { chPeak[c] = a; }
            }
        }

        long stamp = Environment.TickCount64;
        for (int c = 0; c < capCh; c++)
        {
            if (chPeak[c] > ActiveFloor) { _inLastActive[c] = stamp; }
        }

        // Real surround content? Keep every channel. Otherwise it's stereo wearing a 5.1 costume —
        // fold it down and let the upmix decide what the surrounds should get.
        bool contentIsMulti = ActiveLayout(_inLastActive, capCh) > 2;
        int inCh = contentIsMulti ? capCh : 2;
        _inChannels = inCh;

        for (int n = 0; n < frames; n++)
        {
            if (contentIsMulti)
            {
                for (int c = 0; c < inCh; c++)
                {
                    _inScratch[(n * inCh) + c] = src[(n * channels) + c];
                }
            }
            else
            {
                float l = src[n * channels];
                float r = channels >= 2 ? src[(n * channels) + 1] : l;
                _inScratch[n * 2] = l;
                _inScratch[(n * 2) + 1] = r;
            }
        }

        if (contentIsMulti)
        {
            _engine.ProcessMulti(_inScratch, inCh, _outScratch, outCh, frames);
        }
        else
        {
            _engine.Process(_inScratch, 2, _outScratch, outCh, frames);
        }

        int outSamples = frames * outCh;
        float outPeak = 0f;
        Span<float> outChPeak = stackalloc float[8];
        for (int i = 0; i < outSamples; i++)
        {
            float m = Math.Abs(_outScratch[i]);
            if (m > outPeak) { outPeak = m; }
            int c = i % outCh;
            if (c < 8 && m > outChPeak[c]) { outChPeak[c] = m; }
        }

        for (int c = 0; c < outCh && c < 8; c++)
        {
            if (outChPeak[c] > ActiveFloor) { _outLastActive[c] = stamp; }
        }

        // Decay the meter rather than snapping to each block's peak, so it reads like a meter
        // instead of flickering.
        _inPeak = Math.Max(inPeak, _inPeak * 0.75f);
        _outPeak = Math.Max(outPeak, _outPeak * 0.75f);

        // Pack into whatever the device agreed to. Exclusive mode often means integer PCM — NVIDIA's
        // HDMI endpoint takes 16-bit only — so the engine's floats get converted here.
        int written = PackOutput(outSamples);
        buffer.AddSamples(_outBytes, 0, written);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            _running = false;
        }
    }

    /// <summary>Write the engine's floats into <see cref="_outBytes"/> in the negotiated format.
    /// Returns how many bytes were written.</summary>
    private int PackOutput(int outSamples)
    {
        int bits = _outFormat?.BitsPerSample ?? 32;
        bool isFloat = _outIsFloat;

        if (isFloat && bits == 32)
        {
            Span<float> dst = MemoryMarshal.Cast<byte, float>(_outBytes.AsSpan(0, outSamples * 4));
            _outScratch.AsSpan(0, outSamples).CopyTo(dst);
            return outSamples * 4;
        }

        if (bits == 16)
        {
            Span<short> dst = MemoryMarshal.Cast<byte, short>(_outBytes.AsSpan(0, outSamples * 2));
            for (int i = 0; i < outSamples; i++)
            {
                dst[i] = (short)(Math.Clamp(_outScratch[i], -1f, 1f) * 32767f);
            }

            return outSamples * 2;
        }

        if (bits == 24)
        {
            for (int i = 0; i < outSamples; i++)
            {
                int v = (int)(Math.Clamp(_outScratch[i], -1f, 1f) * 8388607f);
                int o = i * 3;
                _outBytes[o] = (byte)v;
                _outBytes[o + 1] = (byte)(v >> 8);
                _outBytes[o + 2] = (byte)(v >> 16);
            }

            return outSamples * 3;
        }

        // 32-bit integer PCM.
        Span<int> ints = MemoryMarshal.Cast<byte, int>(_outBytes.AsSpan(0, outSamples * 4));
        for (int i = 0; i < outSamples; i++)
        {
            // 2147483520 is the largest value representable as a float below int.MaxValue, so the
            // cast can't overflow at full scale.
            ints[i] = (int)(Math.Clamp(_outScratch[i], -1f, 1f) * 2147483520f);
        }

        return outSamples * 4;
    }

    private void EnsureScratch(int frames)
    {
        int inSamples = frames * 8;   // widest input layout we accept
        if (_inScratch.Length < inSamples)
        {
            _inScratch = new float[inSamples];
        }

        int outSamples = frames * _outChannels;
        if (_outScratch.Length < outSamples)
        {
            _outScratch = new float[outSamples];
        }

        int bytes = outSamples * 4;   // widest packing we ever use
        if (_outBytes.Length < bytes)
        {
            _outBytes = new byte[bytes];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.Dispose(); } catch { /* ignore */ }
            _capture = null;
        }

        try { _output?.Dispose(); } catch { /* ignore */ }
        _output = null;
        _buffer = null;
    }
}
