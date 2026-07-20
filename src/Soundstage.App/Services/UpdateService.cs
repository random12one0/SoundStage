using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Soundstage.Core.Update;

namespace Soundstage.App.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Failed,
}

/// <summary>
/// Orchestrates the update flow: check → (if newer) download the installer to a temp file
/// → verify its SHA-256 against the release digest → launch it and exit so it can replace
/// the running app. Everything is best-effort and never crashes the app if offline.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly IUpdateChecker _checker;
    private readonly GitHubUpdateChecker? _gitHubChecker;
    private readonly Version _currentVersion;

    public UpdateService(string owner, string repo, Version currentVersion)
    {
        _gitHubChecker = new GitHubUpdateChecker(owner, repo);
        _checker = _gitHubChecker;
        _currentVersion = currentVersion;
    }

    /// <summary>Test constructor: inject any checker (no network).</summary>
    public UpdateService(IUpdateChecker checker, Version currentVersion)
    {
        _checker = checker;
        _currentVersion = currentVersion;
    }

    public UpdateState State { get; private set; } = UpdateState.Idle;

    public UpdateInfo? Latest { get; private set; }

    public string StatusMessage { get; private set; } = "";

    public double DownloadProgress { get; private set; }

    public Version CurrentVersion => _currentVersion;

    public event Action? Changed;

    /// <summary>Checks GitHub for a newer release. Safe to call on startup in the background.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        SetState(UpdateState.Checking, "Checking for updates…");
        var latest = await _checker.GetLatestAsync(cancellationToken).ConfigureAwait(false);

        if (latest is null)
        {
            if (_gitHubChecker?.LastCheckNotFound == true)
            {
                SetState(UpdateState.Failed, "Couldn't find releases. If the repository is private, make it public to enable in-app updates.");
            }
            else
            {
                SetState(UpdateState.Failed, "Couldn't reach GitHub. Check your connection and try again.");
            }

            return;
        }

        Latest = latest;
        if (UpdatePolicy.IsNewer(_currentVersion, latest.Version))
        {
            var size = latest.PreferredAsset is { } a ? $" ({UpdatePolicy.FormatSize(a.SizeBytes)})" : "";
            SetState(UpdateState.Available, $"Update available: {latest.Name}{size}");
        }
        else
        {
            SetState(UpdateState.UpToDate, $"You're on the latest version ({UpdatePolicy.Format(_currentVersion)}).");
        }
    }

    public bool IsUpdateAvailable => State == UpdateState.Available && Latest is not null;

    /// <summary>
    /// Downloads the preferred asset, verifies it, launches it, and requests app shutdown.
    /// Returns the path launched, or null on failure (with StatusMessage set).
    /// </summary>
    public async Task<string?> DownloadAndInstallAsync(Action requestShutdown, CancellationToken cancellationToken = default)
    {
        var asset = Latest?.PreferredAsset;
        if (asset is null)
        {
            SetState(UpdateState.Failed, "This release has no downloadable installer.");
            return null;
        }

        try
        {
            SetState(UpdateState.Downloading, $"Downloading {asset.Name}…");
            var tempPath = Path.Combine(Path.GetTempPath(), $"Soundstage-update-{Latest!.TagName}-{asset.Name}");
            await DownloadAsync(asset, tempPath, cancellationToken).ConfigureAwait(false);

            if (asset.Sha256 is { } expected && !await VerifyShaAsync(tempPath, expected, cancellationToken).ConfigureAwait(false))
            {
                SetState(UpdateState.Failed, "The downloaded file failed its integrity check and was not run.");
                TryDelete(tempPath);
                return null;
            }

            SetState(UpdateState.ReadyToInstall, "Starting the installer…");
            LaunchInstaller(tempPath);
            requestShutdown();
            return tempPath;
        }
        catch (Exception ex)
        {
            SetState(UpdateState.Failed, $"Update failed: {ex.Message}");
            return null;
        }
    }

    private async Task DownloadAsync(ReleaseAsset asset, string destination, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.SizeBytes;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            read += n;
            if (total > 0)
            {
                DownloadProgress = Math.Clamp((double)read / total, 0, 1);
                Changed?.Invoke();
            }
        }
    }

    private static async Task<bool> VerifyShaAsync(string path, string expectedHex, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static void LaunchInstaller(string path)
    {
        // The Inno installer closes the running app, replaces it, and can relaunch.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // temp file; ignore
        }
    }

    private void SetState(UpdateState state, string message)
    {
        State = state;
        StatusMessage = message;
        Changed?.Invoke();
    }
}
