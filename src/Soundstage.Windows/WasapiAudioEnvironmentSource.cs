using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Soundstage.Core.Abstractions;
using Soundstage.Core.Automation;

namespace Soundstage.Windows;

/// <summary>
/// Live WASAPI view of the audio environment: default render device, mix format
/// (channels / sample rate / bit depth), spatial-audio engagement (best effort), and which
/// processes currently have active audio sessions.
///
/// Change signals: WASAPI device notifications fire immediately; a lightweight fingerprint
/// poll (default 3 s) catches what has no notification — session start/stop and format
/// changes. Everything is defensive: on an audio-less machine (CI) every probe degrades to
/// an empty snapshot instead of throwing.
/// </summary>
public sealed class WasapiAudioEnvironmentSource : IAudioEnvironmentSource, IDisposable
{
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly TimeSpan _pollInterval;

    private MMDeviceEnumerator? _enumerator;
    private NotificationClient? _notificationClient;
    private Timer? _pollTimer;
    private string _lastFingerprint = "";
    private volatile bool _disposed;

    public WasapiAudioEnvironmentSource(IClock clock, TimeSpan? pollInterval = null)
    {
        _clock = clock;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
    }

    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _enumerator is not null)
            {
                return;
            }

            try
            {
                _enumerator = new MMDeviceEnumerator();
                _notificationClient = new NotificationClient(this);
                _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            }
            catch
            {
                _enumerator = null;
                _notificationClient = null;
            }

            _pollTimer = new Timer(_ => PollForChanges(), null, _pollInterval, _pollInterval);
        }
    }

    public AudioEnvironmentSnapshot GetSnapshot()
    {
        try
        {
            return BuildSnapshot();
        }
        catch
        {
            return AudioEnvironmentSnapshot.Empty(_clock.LocalNow);
        }
    }

    private AudioEnvironmentSnapshot BuildSnapshot()
    {
        MMDeviceEnumerator? enumerator;
        lock (_gate)
        {
            enumerator = _enumerator;
        }

        if (enumerator is null)
        {
            return AudioEnvironmentSnapshot.Empty(_clock.LocalNow);
        }

        MMDevice device;
        try
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            // No default render device (CI runners, remote sessions).
            return AudioEnvironmentSnapshot.Empty(_clock.LocalNow);
        }

        using (device)
        {
            var channels = 2;
            var sampleRate = 48000;
            var bitDepth = 0;
            try
            {
                var format = device.AudioClient.MixFormat;
                channels = format.Channels;
                sampleRate = format.SampleRate;
                bitDepth = format.BitsPerSample;
            }
            catch
            {
                // Keep defaults.
            }

            var processes = GetActiveAudioProcesses(device);
            var spatial = SpatialAudioProbe.Probe(device.ID);

            return new AudioEnvironmentSnapshot(
                _clock.LocalNow,
                device.ID,
                SafeFriendlyName(device),
                channels,
                sampleRate,
                bitDepth,
                spatial,
                processes);
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch
        {
            return "Unknown device";
        }
    }

    private static IReadOnlyList<string> GetActiveAudioProcesses(MMDevice device)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive)
                    {
                        continue;
                    }

                    var pid = (int)session.GetProcessID;
                    if (pid <= 0)
                    {
                        continue;
                    }

                    using var process = Process.GetProcessById(pid);
                    // Ignore background utilities (RGB software, system chimes) so they don't
                    // masquerade as "what's playing" and mis-trigger automations.
                    if (!string.IsNullOrEmpty(process.ProcessName) && !Core.Automation.AudioApps.IsNoise(process.ProcessName))
                    {
                        names.Add(process.ProcessName);
                    }
                }
                catch
                {
                    // Session vanished or access denied — skip it.
                }
            }
        }
        catch
        {
            // Session manager unavailable.
        }

        return names.ToList();
    }

    private void PollForChanges()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var snapshot = GetSnapshot();
            var fingerprint = $"{snapshot.DeviceId}|{snapshot.Channels}|{snapshot.SampleRateHz}|{snapshot.Spatial}|{string.Join(",", snapshot.ActiveAudioProcesses)}";
            if (fingerprint != _lastFingerprint)
            {
                _lastFingerprint = fingerprint;
                Changed?.Invoke();
            }
        }
        catch
        {
            // Never let the poll timer die on a transient failure.
        }
    }

    private void OnDeviceEvent()
    {
        if (!_disposed)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pollTimer?.Dispose();
            if (_enumerator is not null && _notificationClient is not null)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }
                catch
                {
                    // Disposal must never throw.
                }
            }

            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    private sealed class NotificationClient(WasapiAudioEnvironmentSource owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.OnDeviceEvent();

        public void OnDeviceAdded(string pwstrDeviceId) => owner.OnDeviceEvent();

        public void OnDeviceRemoved(string deviceId) => owner.OnDeviceEvent();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render)
            {
                owner.OnDeviceEvent();
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
