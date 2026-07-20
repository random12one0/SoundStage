using System.Net;
using System.Net.Http;
using Soundstage.Core.Update;

namespace Soundstage.App.Services;

/// <summary>
/// Reads the latest release from GitHub's public REST API. Works anonymously only when the
/// repository is public; a private repo returns 404 and we surface a friendly hint.
/// </summary>
public sealed class GitHubUpdateChecker(string owner, string repo) : IUpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>True after a check returned 404 — most likely the repo is private.</summary>
    public bool LastCheckNotFound { get; private set; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub requires a User-Agent; the API version header keeps the shape stable.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Soundstage-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        LastCheckNotFound = false;
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                LastCheckNotFound = true;
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return GitHubReleaseParser.TryParse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null; // offline / timeout — never throw at the caller
        }
    }
}
