using Microsoft.Win32;
using Soundstage.Core.Abstractions;

namespace Soundstage.Windows;

/// <summary>Launch-on-boot via the per-user Run key — no elevation, no scheduled tasks.</summary>
public sealed class StartupManager : IStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName;
    private readonly Func<string> _executablePathProvider;

    public StartupManager(string valueName = "Soundstage", Func<string>? executablePathProvider = null)
    {
        _valueName = valueName;
        _executablePathProvider = executablePathProvider ?? (() => Environment.ProcessPath ?? "");
    }

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(_valueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exe = _executablePathProvider();
            if (!string.IsNullOrEmpty(exe))
            {
                // --tray: start minimized to the tray on boot.
                key.SetValue(_valueName, $"\"{exe}\" --tray");
            }
        }
        else
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }
}
