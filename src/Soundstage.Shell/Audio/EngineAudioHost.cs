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

            _capture = new WasapiLoopbackCapture(captureDevice);
            int rate = _capture.WaveFormat.SampleRate;
            _engine.Prepare(rate);

            // Render in the device's own channel layout so the upmix and the per-speaker trims reach
            // real speakers. The engine writes 2, 6 or 8 channels; anything else falls back to stereo
            // and lets Windows do the mapping.
            _outChannels = 2;
            try
            {
                int deviceChannels = renderDevice.AudioClient.MixFormat.Channels;
                if (deviceChannels == 6 || deviceChannels == 8)
                {
                    _outChannels = deviceChannels;
                }
            }
            catch
            {
                // Device wouldn't tell us — stereo is always safe.
            }

            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, _outChannels);
            _buffer = new BufferedWaveProvider(outFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(200),
                DiscardOnBufferOverflow = true,
            };

            _output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            _output.Init(_buffer);

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

        // De-interleave the captured frame to stereo (first two channels; mono is duplicated).
        ReadOnlySpan<float> src = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * channels * 4));
        float inPeak = 0f;
        for (int n = 0; n < frames; n++)
        {
            float l = src[n * channels];
            float r = channels >= 2 ? src[n * channels + 1] : l;
            _inScratch[n * 2] = l;
            _inScratch[n * 2 + 1] = r;
            float m = Math.Max(Math.Abs(l), Math.Abs(r));
            if (m > inPeak) { inPeak = m; }
        }

        int outCh = _outChannels;
        _engine.Process(_inScratch, 2, _outScratch, outCh, frames);

        // Interleaved floats back to bytes, into the render buffer.
        int outSamples = frames * outCh;
        float outPeak = 0f;
        for (int i = 0; i < outSamples; i++)
        {
            float m = Math.Abs(_outScratch[i]);
            if (m > outPeak) { outPeak = m; }
        }

        // Decay the meter rather than snapping to each block's peak, so it reads like a meter
        // instead of flickering.
        _inPeak = Math.Max(inPeak, _inPeak * 0.75f);
        _outPeak = Math.Max(outPeak, _outPeak * 0.75f);

        Span<float> dst = MemoryMarshal.Cast<byte, float>(_outBytes.AsSpan(0, outSamples * 4));
        _outScratch.AsSpan(0, outSamples).CopyTo(dst);
        buffer.AddSamples(_outBytes, 0, outSamples * 4);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            _running = false;
        }
    }

    private void EnsureScratch(int frames)
    {
        int stereo = frames * 2;
        if (_inScratch.Length < stereo)
        {
            _inScratch = new float[stereo];
        }

        int outSamples = frames * _outChannels;
        if (_outScratch.Length < outSamples)
        {
            _outScratch = new float[outSamples];
        }

        int bytes = outSamples * 4;
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
