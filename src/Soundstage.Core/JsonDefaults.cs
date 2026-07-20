using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soundstage.Core;

/// <summary>Shared JSON conventions for everything Soundstage persists.</summary>
public static class JsonDefaults
{
    /// <summary>Human-editable files (presets, state): indented, camelCase, string enums.</summary>
    public static readonly JsonSerializerOptions Readable = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Bundled catalogs: minified, camelCase, string enums.</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
