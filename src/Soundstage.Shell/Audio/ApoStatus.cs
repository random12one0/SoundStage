using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Soundstage.Shell.Audio;

/// <summary>
/// Reports whether the Soundstage plugin is installed and running, and runs the elevated installer
/// on request.
/// </summary>
/// <remarks>
/// <para>
/// "Plugin mode" is the arrangement where our DSP runs inside Windows' own audio engine instead of
/// the app capturing and re-playing audio through a virtual cable. It is strictly better when
/// available — no third-party install, no default-device juggling, less latency — but installing it
/// needs Administrator, so it can never be automatic.
/// </para>
/// <para>
/// Everything here is read-only and failure-tolerant. The app must stay completely usable when the
/// plugin is absent, because that is the state every user starts in.
/// </para>
/// </remarks>
public static class ApoStatus
{
    private const string Clsid = "{6F3C9A21-4E7B-4B36-9E1D-2A55C0D8E401}";
    private const string RenderBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string ApoRegBase = @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects";
    private const string FxGuid = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    private const string DescProp = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string FriendlyProp = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    private static readonly string[] ModeFxProps = { FxGuid + ",6", FxGuid + ",14" };

    public static string LogPath => @"C:\ProgramData\Soundstage\apo.log";

    /// <summary>Is the COM class registered and present in the audio engine's own APO list?</summary>
    /// <remarks>
    /// Both are checked because either alone is misleading. A plugin can be a perfectly valid
    /// registered COM server that Windows will never load, because the engine consults its own list
    /// first and silently skips anything missing from it.
    /// </remarks>
    public static bool IsRegistered()
    {
        try
        {
            using var com = Registry.ClassesRoot.OpenSubKey($@"CLSID\{Clsid}\InprocServer32");
            using var apo = Registry.LocalMachine.OpenSubKey($@"{ApoRegBase}\{Clsid}");
            return com?.GetValue(null) is string path && File.Exists(path) && apo != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Playback devices currently carrying our plugin.</summary>
    public static List<string> AttachedDevices()
    {
        var found = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RenderBase);
            if (root == null)
            {
                return found;
            }

            foreach (var id in root.GetSubKeyNames())
            {
                using var ep = root.OpenSubKey(id);
                if (ep == null || (ep.GetValue("DeviceState") as int?) != 1)
                {
                    continue;
                }

                using var fx = ep.OpenSubKey("FxProperties");
                if (fx == null)
                {
                    continue;
                }

                if (!ModeFxProps.Any(p => string.Equals(fx.GetValue(p) as string, Clsid,
                                                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                using var props = ep.OpenSubKey("Properties");
                var desc = props?.GetValue(DescProp) as string;
                var friendly = props?.GetValue(FriendlyProp) as string;
                if (desc != null)
                {
                    found.Add(friendly != null ? $"{desc} ({friendly})" : desc);
                }
            }
        }
        catch
        {
            // A locked-down machine can refuse the read. "Unknown" is better than a crash.
        }

        return found;
    }

    /// <summary>
    /// The plugin's last words, if it has run. Used to show the user something concrete rather than
    /// a bare "installed" claim — the log says whether it actually locked a stream.
    /// </summary>
    public static string? LastActivity()
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return null;
            }

            // The file is written by audiodg while we read it, so share generously.
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd()
                          .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Where(l => l.Contains("[lock]") || l.Contains("[stats]"))
                          .ToArray();
            return lines.Length > 0 ? lines[^1].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Launch the installer elevated. Returns false if the user declined the UAC prompt — which is a
    /// normal answer, not an error, and must not be reported as a failure.
    /// </summary>
    public static bool RunInstaller(bool uninstall, string? deviceMatch = null, bool repair = false)
    {
        var script = FindScript();
        if (script == null)
        {
            return false;
        }

        var args = $"-ExecutionPolicy Bypass -NoProfile -File \"{script}\"";
        if (uninstall)
        {
            args += " -Uninstall";
        }
        else if (repair)
        {
            args += " -Repair";
        }
        else if (!string.IsNullOrWhiteSpace(deviceMatch))
        {
            args += $" -Device \"{deviceMatch}\"";
        }

        try
        {
            var psi = new ProcessStartInfo("powershell.exe", args)
            {
                UseShellExecute = true,   // required for the runas verb
                Verb = "runas",
            };
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;   // UAC declined
        }
    }

    /// <summary>Find install-apo.ps1 whether we're running from a build tree or an install.</summary>
    private static string? FindScript()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "install-apo.ps1"),
            Path.Combine(AppContext.BaseDirectory, "apo", "install-apo.ps1"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                @"..\..\..\..\..\native\soundstage-apo\install-apo.ps1")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
