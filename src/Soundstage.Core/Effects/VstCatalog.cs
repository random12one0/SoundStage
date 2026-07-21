namespace Soundstage.Core.Effects;

/// <summary>
/// The bundled/optional VST effect rack. Airwindows plugins (MIT) are bundled and ship with the app;
/// anything not redistributable is marked <c>Bundled = false</c> with a download URL so the app can
/// fetch it on demand. Exact DLL filenames and parameter mappings are populated from verified plugin
/// facts — kept here as data so the compiler and UI stay generic.
/// </summary>
public static class VstCatalog
{
    // Populated once the exact Airwindows DLL names + parameter mappings are verified. The compiler
    // and UI already work against this list; adding entries lights up the rack.
    public static IReadOnlyList<VstRackEffect> All { get; } = [];

    public static VstRackEffect? Get(string id) => All.FirstOrDefault(e => e.Id == id);
}
