using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundstage.Shell.Audio;

/// <summary>
/// Metering and per-speaker volume for a playback device, using only Windows' own APIs — no audio
/// plugin, no virtual cable, nothing in the audio path.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the two things people actually use every day — watching which speakers are
/// active and setting how loud each one is — do not require modifying the audio at all:
/// </para>
/// <list type="bullet">
///   <item><description><b>Levels</b> come from WASAPI loopback capture, which passively reads the
///   samples already being rendered to the device. It never changes them, so it cannot break
///   playback.</description></item>
///   <item><description><b>Per-speaker volume</b> is set through <c>IAudioEndpointVolume</c>'s
///   per-channel controls, which Windows exposes on the endpoint itself — the same mechanism behind
///   the volume mixer's balance. Again, nothing new sits in the stream.</description></item>
/// </list>
/// <para>
/// The one thing this cannot do is reshape the sound (an EQ, night-mode bass cut, a compressor) —
/// that genuinely needs code in the audio path (the plugin). Everything here is deliberately the
/// part that is safe by construction.
/// </para>
/// </remarks>
public sealed class NativeMonitor : IDisposable
{
    private readonly object _gate = new();
    private MMDeviceEnumerator _enum = new();
    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;
    private bool _disposed;

    private readonly float[] _peak = new float[8];
    private int _channels = 2;

    /// <summary>Live per-speaker output levels, 0..1, decayed like a meter. Windows channel order.</summary>
    public float[] Peaks => _peak;

    /// <summary>How many channels the current device is running (2, 6, 8…).</summary>
    public int Channels => _channels;

    /// <summary>The device being monitored, or null if none opened.</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>True while a loopback capture is running and levels are live.</summary>
    public bool IsMonitoring { get; private set; }

    /// <summary>
    /// Point the monitor at a device (by id) or, when id is null, whatever Windows' default output is.
    /// Safe to call repeatedly — it tears down any previous capture first.
    /// </summary>
    public void Start(string? deviceId = null)
    {
        lock (_gate)
        {
            StopInternal();
            try
            {
                _device = deviceId is null
                    ? _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : _enum.GetDevice(deviceId);
                if (_device is null) { return; }

                DeviceName = _device.FriendlyName;
                _channels = Math.Min(8, _device.AudioClient.MixFormat.Channels);

                // Loopback capture: reads the rendered stream, never touches it.
                _capture = new WasapiLoopbackCapture(_device);
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += (_, _) => { IsMonitoring = false; };
                _capture.StartRecording();
                IsMonitoring = true;
            }
            catch
            {
                // A device that can't be captured (asleep, exclusive-held) just yields no meters.
                StopInternal();
            }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture?.WaveFormat;
        if (fmt is null) { return; }

        int ch = Math.Min(8, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frameBytes = bytesPerSample * fmt.Channels;
        int frames = e.BytesRecorded / Math.Max(1, frameBytes);

        Span<float> block = stackalloc float[8];
        block.Clear();

        // Loopback is IEEE float in shared mode.
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * frameBytes;
            for (int c = 0; c < ch; c++)
            {
                float v = BitConverter.ToSingle(e.Buffer, baseIdx + c * bytesPerSample);
                float a = Math.Abs(v);
                if (a > block[c]) { block[c] = a; }
            }
        }

        // Decay toward the block peak, so it reads like a meter rather than flickering.
        for (int c = 0; c < 8; c++)
        {
            float now = c < ch ? block[c] : 0f;
            _peak[c] = now > _peak[c] ? now : _peak[c] * 0.72f;
        }
    }

    /// <summary>Windows' per-channel volume for the current device, 0..1. Empty if unavailable.</summary>
    public float[] GetChannelVolumes()
    {
        lock (_gate)
        {
            try
            {
                var vol = _device?.AudioEndpointVolume;
                if (vol is null) { return Array.Empty<float>(); }
                int n = vol.Channels.Count;
                var r = new float[n];
                for (int i = 0; i < n; i++) { r[i] = vol.Channels[i].VolumeLevelScalar; }
                return r;
            }
            catch { return Array.Empty<float>(); }
        }
    }

    /// <summary>
    /// Set one speaker's volume, 0..1, through Windows' own per-channel endpoint volume. This is the
    /// per-speaker "trim" — no plugin involved, and it survives app restarts because Windows stores it.
    /// </summary>
    public void SetChannelVolume(int channel, float scalar)
    {
        lock (_gate)
        {
            try
            {
                var vol = _device?.AudioEndpointVolume;
                if (vol is null || channel < 0 || channel >= vol.Channels.Count) { return; }
                vol.Channels[channel].VolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
            }
            catch
            {
                // Some endpoints don't allow per-channel volume; then this is a no-op and the fader
                // simply doesn't move that speaker. Nothing breaks.
            }
        }
    }

    /// <summary>Number of per-channel volumes Windows lets us set on this device (0 if none).</summary>
    public int VolumeChannelCount
    {
        get
        {
            lock (_gate)
            {
                try { return _device?.AudioEndpointVolume?.Channels.Count ?? 0; }
                catch { return 0; }
            }
        }
    }

    private void StopInternal()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        IsMonitoring = false;
        for (int i = 0; i < 8; i++) { _peak[i] = 0f; }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        lock (_gate)
        {
            StopInternal();
            _device?.Dispose();
            _enum.Dispose();
        }
    }
}
