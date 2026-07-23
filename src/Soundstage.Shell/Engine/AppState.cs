using System.IO;

namespace Soundstage.Shell.Engine;

/// <summary>
/// Where the UI's settings live between runs.
///
/// The web UI owns the state model, so rather than mirror it in C# (two models that drift apart) we
/// store the page's own JSON verbatim and hand it back on the next launch. The app never interprets
/// it — the page does. A corrupt or missing file just means "start from the defaults", which is why
/// every path here fails quietly.
/// </summary>
public static class AppState
{
    private static readonly object Sync = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Soundstage",
        "state.json");

    /// <summary>Persist the UI state. Written to a temp file and moved into place, so an interrupted
    /// write can't leave a half-truncated file that fails to load next launch.</summary>
    public static void Save(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                string dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
            }
        }
        catch
        {
            // Disk full, locked file, roaming profile weirdness — never take the app down over it.
        }
    }

    /// <summary>The saved UI state, or null if there isn't one (first run) or it can't be read.</summary>
    public static string? Load()
    {
        try
        {
            lock (Sync)
            {
                return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            }
        }
        catch
        {
            return null;
        }
    }
}
