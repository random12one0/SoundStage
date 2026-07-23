using System.Diagnostics;

using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Soundstage.Shell.Audio;

/// <summary>
/// Which applications are actually making sound right now.
///
/// This is what lets "when Netflix is playing" mean something. Windows exposes an audio session per
/// process on each endpoint; we look at the outlet (where everything is playing, once routing is set
/// up) and report the processes whose session is active rather than merely open — an app that is
/// paused shouldn't trigger anything.
/// </summary>
public static class SessionWatcher
{
    public sealed record Session(string Process, string Title, bool Active);

    /// <summary>Sessions on every render endpoint, deduplicated by process.</summary>
    public static IReadOnlyList<Session> Active()
    {
        var byProcess = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var mm = new MMDeviceEnumerator();
            foreach (MMDevice device in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                SessionCollection sessions;
                try { sessions = device.AudioSessionManager.Sessions; }
                catch { continue; }

                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl session;
                    try { session = sessions[i]; }
                    catch { continue; }

                    try
                    {
                        if (session.State == AudioSessionState.AudioSessionStateExpired)
                        {
                            continue;
                        }

                        bool active = session.State == AudioSessionState.AudioSessionStateActive;

                        // A session can be "active" while silent; the peak meter tells us whether
                        // sound is really coming out of it.
                        float peak = 0f;
                        try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { /* ignore */ }
                        active = active && peak > 0.0005f;

                        string name = ProcessName((int)session.GetProcessID);
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        string title = session.DisplayName;
                        if (string.IsNullOrWhiteSpace(title)) { title = name; }

                        if (byProcess.TryGetValue(name, out Session? existing) && existing.Active)
                        {
                            continue;   // already recorded as playing on another endpoint
                        }

                        byProcess[name] = new Session(name, title, active);
                    }
                    catch
                    {
                        // Sessions vanish mid-enumeration all the time; skip and carry on.
                    }
                }
            }
        }
        catch
        {
            // Enumeration failed entirely — report nothing rather than throwing at the caller.
        }

        return byProcess.Values.ToList();
    }

    private static string ProcessName(int pid)
    {
        if (pid <= 0)
        {
            return "";
        }

        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }
}
