using Soundstage.Core.Apo;

namespace Soundstage.Core.Diagnostics;

/// <summary>
/// The Atmos audibility test: an EQ nobody could miss. If enabling it audibly muffles the
/// sound while Dolby Atmos for Home Theater is active, Equalizer APO is really in the
/// signal path; if nothing changes, spatial processing is bypassing APO and the user needs
/// the SFX/EFX install-mode fix (or must disable Dolby Access).
/// </summary>
public static class DiagnosticSweep
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(8);

    /// <summary>Chain content applied during the test. No Device: scoping — hits every endpoint.</summary>
    public static string BuildChainText()
    {
        var doc = new ApoDocument([
            new CommentCommand("Soundstage diagnostic — TEMPORARY extreme EQ, auto-reverts."),
            new FilterCommand(FilterType.HighShelf, 1000, -18, 0.707),
            new PreampCommand(-3),
        ]);
        return doc.Render();
    }
}
