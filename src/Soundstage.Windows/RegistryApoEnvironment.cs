using Microsoft.Win32;
using Soundstage.Core.Abstractions;

namespace Soundstage.Windows;

/// <summary>
/// Locates Equalizer APO via its registry key
/// (<c>HKLM\SOFTWARE\EqualizerAPO</c>, value <c>ConfigPath</c>).
/// </summary>
public sealed class RegistryApoEnvironment : IApoEnvironment
{
    private const string KeyPath = @"SOFTWARE\EqualizerAPO";
    private const string ConfigPathValue = "ConfigPath";

    public bool IsApoInstalled => ConfigDirectory is not null;

    public string? ConfigDirectory
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath)
                                ?? Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
                var path = key?.GetValue(ConfigPathValue) as string;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }
    }
}
