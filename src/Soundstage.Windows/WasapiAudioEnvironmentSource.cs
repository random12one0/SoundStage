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

            var (processes, sourceChannels) = GetActiveAudio(device);
            var nowPlaying = BuildNowPlaying(processes);
            var spatial = SpatialAudioProbe.Probe(device.ID);

            return new AudioEnvironmentSnapshot(
                _clock.LocalNow,
                device.ID,
                SafeFriendlyName(device),
                channels,
                sampleRate,
                bitDepth,
                spatial,
                processes,
                nowPlaying,
                sourceChannels);
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

    private static (IReadOnlyList<string> Processes, int SourceChannels) GetActiveAudio(MMDevice device)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceChannels = 0;
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
                    if (string.IsNullOrEmpty(process.ProcessName) || Core.Automation.AudioApps.IsNoise(process.ProcessName))
                    {
                        continue;
                    }

                    names.Add(process.ProcessName);

                    // The session meter reports the app's own stream channel count — the SOURCE
                    // format (2 = stereo, 6 = 5.1, 8 = 7.1). Take the loudest/widest so "5.1
                    // content is playing" wins over a silent stereo browser tab. Best-effort.
                    try
                    {
                        var channels = session.AudioMeterInformation.PeakValues.Count;
                        if (channels > sourceChannels)
                        {
                            sourceChannels = channels;
                        }
                    }
                    catch
                    {
                        // Per-session metering unavailable — leave source channels unknown.
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

        return (names.ToList(), sourceChannels);
    }

    /// <summary>
    /// Friendly "what's playing" labels: browsers are resolved to the streaming service in their
    /// tab title (so "chrome" becomes "YouTube"), everything else uses its pretty app name. All
    /// best-effort — reading another process's window title can fail, and we never throw.
    /// </summary>
    private static IReadOnlyList<string> BuildNowPlaying(IReadOnlyList<string> processes)
    {
        var labels = new List<string>();
        foreach (var name in processes)
        {
            var label = AudioApps.IsBrowser(name) && DetectBrowserService(name) is { } service
                ? service
                : AudioApps.Pretty(name);

            if (!labels.Contains(label))
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    /// <summary>
    /// The streaming service showing in a browser's active tab, read from its main window title.
    /// The audio session usually belongs to a title-less renderer child, so we scan every process
    /// of that browser and take the first recognisable service. Best-effort and defensive.
    /// </summary>
    private static string? DetectBrowserService(string processName)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (AudioApps.DetectStreamingService(process.MainWindowTitle) is { } service)
                    {
                        return service;
                    }
                }
                catch
                {
                    // MainWindowTitle can throw for an exiting process — skip it.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Process enumeration failed — no enrichment this round.
        }

        return null;
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
            var fingerprint = $"{snapshot.DeviceId}|{snapshot.Channels}|{snapshot.SampleRateHz}|{snapshot.Spatial}|{snapshot.SourceChannels}|{string.Join(",", snapshot.ActiveAudioProcesses)}|{string.Join(",", snapshot.NowPlaying)}";
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
