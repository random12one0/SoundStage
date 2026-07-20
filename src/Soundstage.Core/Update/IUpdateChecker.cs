namespace Soundstage.Core.Update;

/// <summary>Fetches the latest release from a source (GitHub in the shipping app; a fake in tests).</summary>
public interface IUpdateChecker
{
    /// <summary>Returns the latest release, or null if it can't be reached / has no valid release.</summary>
    Task<UpdateInfo?> GetLatestAsync(CancellationToken cancellationToken = default);
}
