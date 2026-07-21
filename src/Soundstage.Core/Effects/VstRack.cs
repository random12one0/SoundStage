using System.Globalization;

namespace Soundstage.Core.Effects;

/// <summary>
/// One VST parameter driven by the effect's 0–100 intensity slider. VST2 parameters are normalized
/// 0..1; <see cref="AtZero"/>/<see cref="AtFull"/> are the normalized values at 0% and 100%, and the
/// shared front-loaded curve fills in between.
/// </summary>
public sealed record VstIntensityParam(string Name, double AtZero, double AtFull)
{
    public double ValueAt(int intensity) =>
        AtZero + (AtFull - AtZero) * IntensityCurve.Fraction(Math.Clamp(intensity, 0, 100));
}

/// <summary>
/// A catalog definition of one rack effect: a bundled (MIT Airwindows) or optional plugin, where to
/// route it (a whole layout or a single channel like the subwoofer/centre), and how its intensity
/// slider maps onto plugin parameters. The DLL is resolved to an absolute path at compile time; if
/// it isn't installed yet, the effect is simply skipped.
/// </summary>
public sealed record VstRackEffect(
    string Id,
    string Name,
    string Description,
    string DllFileName,
    string ChannelRoute,
    IReadOnlyList<VstIntensityParam> IntensityParams,
    IReadOnlyList<(string Name, double Value)>? FixedParams = null,
    bool Bundled = true,
    string? DownloadUrl = null,
    string? LicenseNote = null)
{
    public const string RouteAll = "all";
    public const string RouteSub = "LFE";
    public const string RouteCentre = "C";

    /// <summary>Renders the argument tail for a <c>VSTPlugin:</c> line at the given intensity.</summary>
    public string BuildArguments(int intensity)
    {
        var parts = new List<string>();
        foreach (var (name, value) in FixedParams ?? [])
        {
            parts.Add(Param(name, value));
        }

        foreach (var p in IntensityParams)
        {
            parts.Add(Param(p.Name, p.ValueAt(intensity)));
        }

        return string.Join(' ', parts);
    }

    private static string Param(string name, double value) =>
        $"\"{name}\" {value.ToString("0.####", CultureInfo.InvariantCulture)}";
}

/// <summary>Per-profile saved state for a rack effect (what the user toggled / set).</summary>
public sealed record VstRackEntry(string Id, bool Enabled = false, int Intensity = 50);
