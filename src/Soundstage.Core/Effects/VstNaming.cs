using System.Text;
using System.Text.RegularExpressions;

namespace Soundstage.Core.Effects;

/// <summary>
/// Lenient matching for VST DLL filenames. Airwindows ships its 64-bit VST2 builds with a "64"
/// suffix (<c>BassKit64.dll</c>) while older packs and our catalog once used the bare name
/// (<c>BassKit.dll</c>); users also drop files with odd casing, spaces, or "(1)" copies. We reduce
/// every name to a canonical key — lowercase alphanumerics with a trailing 64-bit build tag
/// stripped — so all of those forms resolve to the same effect.
/// </summary>
public static class VstNaming
{
    /// <summary>
    /// Canonical key for a DLL filename (path, extension, casing, and punctuation all ignored, and a
    /// trailing "64" build tag removed). Two names that name the same plugin share a key.
    /// </summary>
    public static string NormalizeKey(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);

        // Drop a trailing Windows "(1)" copy marker before anything else, so a re-downloaded
        // "BassKit64 (1).dll" still matches "BassKit64.dll".
        name = Regex.Replace(name, @"\s*\(\d+\)\s*$", string.Empty);

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        var key = sb.ToString();

        // Strip the Airwindows x64 build tag ("BassKit64" -> "basskit"), but only when something
        // meaningful remains, so a plugin genuinely named "…64" isn't erased.
        if (key.Length > 2 && key.EndsWith("64", StringComparison.Ordinal))
        {
            key = key[..^2];
        }

        return key;
    }

    /// <summary>True when two filenames name the same plugin under lenient matching.</summary>
    public static bool Matches(string wantedDll, string candidateFile) =>
        NormalizeKey(wantedDll) == NormalizeKey(candidateFile) && NormalizeKey(wantedDll).Length > 0;
}
