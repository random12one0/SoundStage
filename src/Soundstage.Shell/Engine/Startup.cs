using Microsoft.Win32;

namespace Soundstage.Shell.Engine;

/// <summary>
/// The "Launch at login" setting, done the way a desktop app should: a per-user Run entry. No admin
/// rights, no scheduled task, and uninstalling the app leaves nothing behind but this one value.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Soundstage";

    public static void SetRunAtLogin(bool on)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                                    ?? throw new InvalidOperationException("Run key unavailable.");
            if (on)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    return;
                }

                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Locked-down machine or a policy-managed Run key — the toggle just doesn't stick.
        }
    }

    public static bool IsRunAtLogin()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }
}
