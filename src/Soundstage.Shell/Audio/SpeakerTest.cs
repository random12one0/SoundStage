using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundstage.Shell.Audio;

/// <summary>
/// Plays a short test burst out of one speaker, so "which one is my centre?" has an actual answer.
///
/// It talks to the render device directly rather than going through the engine: the point of a
/// calibration tone is to hear that one physical speaker, not to hear it through whatever effects
/// happen to be switched on. It does honour the channel trim, because checking a trim is the whole
/// reason you press the button.
///
/// The output device is opened once and held, with a source that is silent between bursts. Tearing
/// the endpoint down and reopening it per burst is racy — pressing two Test buttons in quick
/// succession (or Test all) would drop bursts on the floor. An idle timer closes the device once
/// you stop pressing, so we don't hold a receiver awake forever.
/// </summary>
public sealed class SpeakerTest : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(6);

    private readonly object _sync = new();
    private readonly System.Threading.Timer _idle;

    private WasapiOut? _out;
    private BurstSource? _source;
    private string? _deviceId;
    private int _channels;
    private bool _disposed;

    public SpeakerTest()
    {
        _idle = new System.Threading.Timer(_ => CloseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Fire ~600 ms of soft noise from <paramref name="channel"/> (7.1 order) on
    /// <paramref name="device"/>. Returns false if that device has no such speaker, which is the
    /// honest answer rather than a button that flashes at nothing.
    /// </summary>
    public bool Play(MMDevice device, int channel, double trimDb)
    {
        ArgumentNullException.ThrowIfNull(device);

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            if (!EnsureOpen(device))
            {
                return false;
            }

            if (channel < 0 || channel >= _channels)
            {
                return false;
            }

            _source!.Trigger(channel, trimDb);
            _idle.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    private bool EnsureOpen(MMDevice device)
    {
        if (_out is not null && string.Equals(_deviceId, device.ID, StringComparison.Ordinal))
        {
            return true;
        }

        Close();

        try
        {
            _channels = device.AudioClient.MixFormat.Channels;
            int rate = device.AudioClient.MixFormat.SampleRate;
            _source = new BurstSource(rate, _channels);
            _out = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 60);
            _out.Init(_source);
            _out.Play();
            _deviceId = device.ID;
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    private void CloseIfIdle()
    {
        lock (_sync)
        {
            if (_source is not null && _source.IsBursting)
            {
                _idle.Change(IdleTimeout, Timeout.InfiniteTimeSpan);   // still going — check again later
                return;
            }

            Close();
        }
    }

    private void Close()
    {
        try { _out?.Stop(); } catch { /* tearing down */ }
        try { _out?.Dispose(); } catch { /* tearing down */ }
        _out = null;
        _source = null;
        _deviceId = null;
        _channels = 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close();
        }

        _idle.Dispose();
    }

    /// <summary>
    /// Silent until triggered, then one burst of low-passed noise on a single channel with a raised
    /// cosine fade at both ends so it never clicks. The audio thread reads these fields while the UI
    /// thread writes them; a burst that starts a buffer late is imperceptible, so no lock is taken on
    /// the audio path.
    /// </summary>
    private sealed class BurstSource : ISampleProvider
    {
        private readonly int _channels;
        private readonly int _burstFrames;
        private readonly int _fadeFrames;
        private readonly Random _rng = new(12345);   // fixed seed: every test sounds the same

        private volatile int _channel = -1;
        private volatile float _gain;
        private long _frame;
        private long _endFrame;
        private float _lp;

        public BurstSource(int rate, int channels)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
            _channels = channels;
            _burstFrames = (int)(rate * 0.6);
            _fadeFrames = rate / 50;   // 20 ms in and out
        }

        public WaveFormat WaveFormat { get; }

        public bool IsBursting => Interlocked.Read(ref _frame) < Interlocked.Read(ref _endFrame);

        public void Trigger(int channel, double trimDb)
        {
            _gain = (float)(0.4 * Math.Pow(10.0, trimDb / 20.0));
            _channel = channel;
            Interlocked.Exchange(ref _endFrame, Interlocked.Read(ref _frame) + _burstFrames);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / _channels;
            Array.Clear(buffer, offset, count);

            int ch = _channel;
            float gain = _gain;
            long end = Interlocked.Read(ref _endFrame);

            for (int n = 0; n < frames; n++)
            {
                long f = Interlocked.Increment(ref _frame) - 1;
                if (ch < 0 || ch >= _channels || f >= end)
                {
                    continue;
                }

                // White noise through a one-pole low-pass — softer, and easier to place by ear.
                float white = (float)((_rng.NextDouble() * 2.0) - 1.0);
                _lp += 0.22f * (white - _lp);

                long into = f - (end - _burstFrames);
                float env = 1f;
                if (into < _fadeFrames) { env = into / (float)_fadeFrames; }
                else if (into > _burstFrames - _fadeFrames) { env = (_burstFrames - into) / (float)_fadeFrames; }

                env = 0.5f * (1f - MathF.Cos(MathF.PI * Math.Clamp(env, 0f, 1f)));   // raised cosine
                buffer[offset + (n * _channels) + ch] = _lp * gain * env;
            }

            return count;
        }
    }
}
