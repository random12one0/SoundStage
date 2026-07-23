using System.Diagnostics;

using NAudio.CoreAudioApi;

namespace Soundstage.Shell.Audio;

/// <summary>
/// What Windows knows about a playback device — and, more usefully, the two different answers it
/// gives.
///
/// A receiver reports the speakers it physically has (PKEY_AudioEndpoint_PhysicalSpeakers), but
/// Windows only ever hands an app the layout the user picked in "Configure Speakers". A 5.1 receiver
/// left on the stereo default looks like a stereo device to every app on the system. We surface both
/// numbers so the app can say "your receiver is 5.1 but Windows is set to stereo" instead of
/// silently pretending you own two speakers.
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    int Channels,          // what Windows will actually give us right now
    int PhysicalChannels,  // what the hardware says it has
    string Layout,         // e.g. "stereo"
    string PhysicalLayout, // e.g. "5.1 surround"
    bool IsDefault,
    bool IsCable);

public static class AudioDevices
{
    // PKEY_AudioEndpoint_PhysicalSpeakers — the speaker mask the endpoint reports.
    private static readonly PropertyKey PhysicalSpeakersKey =
        new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 3);

    public static bool IsCable(string name)
        => name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Soundstage", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AudioDeviceInfo> Render()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var mm = new MMDeviceEnumerator();
            string defaultId = "";
            try { defaultId = mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
            catch { /* no default device */ }

            foreach (MMDevice d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                int channels = 2;
                try { channels = d.AudioClient.MixFormat.Channels; } catch { /* keep the default */ }

                int physical = PhysicalChannelCount(d, channels);

                list.Add(new AudioDeviceInfo(
                    d.ID,
                    d.FriendlyName,
                    channels,
                    physical,
                    LayoutName(channels),
                    LayoutName(physical),
                    string.Equals(d.ID, defaultId, StringComparison.Ordinal),
                    IsCable(d.FriendlyName)));
            }
        }
        catch
        {
            // Endpoints can be mid-change; the caller keeps whatever it already had.
        }

        return list;
    }

    /// <summary>How many speakers the hardware claims, from its physical speaker mask. Falls back to
    /// the configured count when the device doesn't report one (most non-HDMI devices don't).</summary>
    private static int PhysicalChannelCount(MMDevice device, int fallback)
    {
        try
        {
            if (!device.Properties.Contains(PhysicalSpeakersKey))
            {
                return fallback;
            }

            object value = device.Properties[PhysicalSpeakersKey].Value;
            uint mask = value switch
            {
                uint u => u,
                int i => (uint)i,
                _ => 0u,
            };

            if (mask == 0)
            {
                return fallback;
            }

            int count = System.Numerics.BitOperations.PopCount(mask);
            return count < fallback ? fallback : count;
        }
        catch
        {
            return fallback;
        }
    }

    public static string LayoutName(int channels) => channels switch
    {
        >= 8 => "7.1 surround",
        >= 6 => "5.1 surround",
        >= 4 => "quadraphonic",
        _ => "stereo",
    };

    public static string LayoutShort(int channels) => channels switch
    {
        >= 8 => "7.1",
        >= 6 => "5.1",
        >= 4 => "4.0",
        _ => "2.0",
    };

    /// <summary>
    /// Open Windows' Sound control panel on the Playback tab, where "Configure" lives. We deliberately
    /// don't change the speaker layout ourselves — it's a system setting, and it's the user's call.
    /// </summary>
    public static void OpenWindowsSpeakerSetup()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,0",
                UseShellExecute = true,
            });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
            catch { /* nothing else to try */ }
        }
    }
}
