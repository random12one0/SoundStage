using NAudio.CoreAudioApi;
using Soundstage.Core.Abstractions;

namespace Soundstage.Windows;

/// <summary>
/// Polls the default render endpoint's <c>IAudioMeterInformation</c> (~30 Hz) for the live
/// headroom meter and clip warning. No capture stream is opened — this is the same cheap
/// meter the Windows volume mixer uses. Self-disables after repeated failures.
/// </summary>
public sealed class PeakMeterService : IPeakMeter
{
    private const int PollMilliseconds = 33;
    private const float ClipThreshold = 0.999f;
    private const int ClipConsecutivePolls = 2;
    private const int MaxConsecutiveFailures = 10;

    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private Timer? _timer;
    private int _consecutiveClips;
    private int _consecutiveFailures;

    public float LatestPeak { get; private set; } = float.NaN;

    public bool IsRunning { get; private set; }

    public event Action<float>? PeakUpdated;

    public event Action? ClippingDetected;

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return;
            }

            try
            {
                _enumerator = new MMDeviceEnumerator();
            }
            catch
            {
                return; // No audio subsystem — meter stays off.
            }

            IsRunning = true;
            _timer = new Timer(_ => Poll(), null, PollMilliseconds, PollMilliseconds);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            IsRunning = false;
            _timer?.Dispose();
            _timer = null;
            _device?.Dispose();
            _device = null;
            _enumerator?.Dispose();
            _enumerator = null;
            LatestPeak = float.NaN;
        }
    }

    /// <summary>Drops the cached device so the next poll rebinds (call on device change).</summary>
    public void RebindDevice()
    {
        lock (_gate)
        {
            _device?.Dispose();
            _device = null;
        }
    }

    private void Poll()
    {
        float peak;
        try
        {
            lock (_gate)
            {
                if (!IsRunning || _enumerator is null)
                {
                    return;
                }

                _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                peak = _device.AudioMeterInformation.MasterPeakValue;
            }

            _consecutiveFailures = 0;
        }
        catch
        {
            RebindDevice();
            if (++_consecutiveFailures >= MaxConsecutiveFailures)
            {
                Stop();
            }

            return;
        }

        LatestPeak = peak;
        PeakUpdated?.Invoke(peak);

        if (peak >= ClipThreshold)
        {
            if (++_consecutiveClips == ClipConsecutivePolls)
            {
                ClippingDetected?.Invoke();
            }
        }
        else
        {
            _consecutiveClips = 0;
        }
    }

    public void Dispose() => Stop();
}
