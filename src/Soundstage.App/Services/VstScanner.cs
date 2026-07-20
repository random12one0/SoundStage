using System.IO;

namespace Soundstage.App.Services;

/// <summary>
/// Finds already-installed 64-bit VST 2 compressor DLLs in the standard locations (plus
/// the Downloads folder, since LoudMax ships as a bare DLL). Licensing keeps us from
/// bundling a compressor; this makes "one download" the only manual step.
/// </summary>
public static class VstScanner
{
    /// <summary>Known free compressor/limiter DLL name fragments, in preference order.</summary>
    private static readonly string[] KnownNames = ["loudmax", "kotelnikov", "roughrider", "rough rider", "mcompressor"];

    public static IReadOnlyList<string> FindCompressors()
    {
        var results = new List<(string Path, int NameRank, int Bitness)>();

        foreach (var directory in CandidateDirectories())
        {
            try
            {
                if (!Directory.Exists(directory.Path))
                {
                    continue;
                }

                var option = directory.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(directory.Path, "*.dll", option))
                {
                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    var rank = Array.FindIndex(KnownNames, k => name.Contains(k));
                    if (rank < 0)
                    {
                        continue;
                    }

                    // Equalizer APO hosts 64-bit VSTs; prefer names marked 64, then unmarked, then 32-marked last.
                    var bitness = name.Contains("64") ? 0 : name.Contains("32") || name.Contains("x86") ? 2 : 1;
                    results.Add((file, rank, bitness));
                }
            }
            catch
            {
                // Access denied / IO problems on a candidate folder — just move on.
            }
        }

        return results
            .OrderBy(r => r.NameRank)
            .ThenBy(r => r.Bitness)
            .ThenBy(r => r.Path.Length)
            .Select(r => r.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<(string Path, bool Recursive)> CandidateDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var commonProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return (Path.Combine(programFiles, "VSTPlugins"), true);
        yield return (Path.Combine(programFiles, "Steinberg", "VSTPlugins"), true);
        yield return (Path.Combine(commonProgramFiles, "VST2"), true);
        yield return (Path.Combine(commonProgramFiles, "Steinberg", "VST2"), true);
        yield return (Path.Combine(userProfile, "Downloads"), false);
        yield return (Path.Combine(userProfile, "Desktop"), false);
    }
}
