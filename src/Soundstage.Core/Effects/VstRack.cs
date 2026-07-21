namespace Soundstage.Core.Effects;

/// <summary>
/// Maps the 0–100 intensity slider onto one plugin parameter (by index). VST2 parameters are
/// normalized 0..1; <see cref="AtZero"/>/<see cref="AtFull"/> are the values at 0% and 100%, with the
/// shared front-loaded curve in between.
/// </summary>
public sealed record VstIntensityMap(int Index, double AtZero, double AtFull);

/// <summary>
/// A catalog definition of one rack effect. Airwindows plugins are "chunk" plugins, so Equalizer APO
/// ignores named parameters and only honours a <c>ChunkData</c> blob — which for Airwindows is simply
/// the little-endian float32 array of every parameter, in order. We therefore carry the full default
/// parameter array and reproduce that blob deterministically (no need to run the plugin), letting the
/// intensity slider move just the parameters in <see cref="IntensityMaps"/>.
/// </summary>
public sealed record VstRackEffect(
    string Id,
    string Name,
    string Description,
    string DllFileName,
    string ChannelRoute,
    IReadOnlyList<double> DefaultParams,
    IReadOnlyList<VstIntensityMap> IntensityMaps,
    bool Bundled = true,
    string? DownloadUrl = null,
    string? LicenseNote = null)
{
    /// <summary>Front stereo pair — the safe scope for stereo plugins on any layout.</summary>
    public const string RouteFrontStereo = "L R";

    /// <summary>The parameter array at a given intensity (defaults with the mapped params moved).</summary>
    public float[] ParamsAt(int intensity)
    {
        var frac = IntensityCurve.Fraction(Math.Clamp(intensity, 0, 100));
        var values = DefaultParams.Select(v => (float)v).ToArray();
        foreach (var map in IntensityMaps)
        {
            if (map.Index >= 0 && map.Index < values.Length)
            {
                values[map.Index] = (float)(map.AtZero + (map.AtFull - map.AtZero) * frac);
            }
        }

        return values;
    }

    /// <summary>
    /// The Equalizer APO <c>ChunkData</c> blob at a given intensity: base64 of the parameter array as
    /// little-endian float32, no header — exactly what an Airwindows <c>getChunk</c> produces.
    /// </summary>
    public string BuildChunkData(int intensity)
    {
        var values = ParamsAt(intensity);
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            // x64 is little-endian on both the build host and Windows, matching Airwindows' format.
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float)), values[i]);
        }

        return Convert.ToBase64String(bytes);
    }
}

/// <summary>Per-profile saved state for a rack effect (what the user toggled / set).</summary>
public sealed record VstRackEntry(string Id, bool Enabled = false, int Intensity = 50);
