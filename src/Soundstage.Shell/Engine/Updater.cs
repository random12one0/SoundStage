using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Soundstage.Shell.Engine;

/// <summary>
/// Checks GitHub for a newer release and, if you ask it to, downloads one.
///
/// It only ever *offers*. Downloading happens when you press the button, and the installer is
/// launched, never silently run — an audio app that swaps itself out from under you while you are
/// listening to something is not a good neighbour. Everything here fails quietly: no network, a rate
/// limit, or a malformed release just means "no update available", which is the safe answer.
/// </summary>
public static class Updater
{
    private const string ReleasesApi = "https://api.github.com/repos/random12one0/SoundStage/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub rejects requests without a user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Soundstage-Updater");
        return http;
    }

    public static string CurrentVersion
    {
        get
        {
            try
            {
                var info = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                string raw = info ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
                int plus = raw.IndexOf('+');   // strip the build metadata SDKs append
                return plus > 0 ? raw[..plus] : raw;
            }
            catch
            {
                return "1.0.0";
            }
        }
    }

    public sealed record Result(bool Available, string Current, string Latest, string Notes, string Url, string AssetUrl);

    /// <summary>Ask GitHub what the newest release is. Never throws.</summary>
    public static async Task<Result> CheckAsync()
    {
        string current = CurrentVersion;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(ReleasesApi).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new Result(false, current, current, "", "", "");
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tag = Str(root, "tag_name");
            string latest = tag.TrimStart('v', 'V');
            string notes = Str(root, "body");
            string page = Str(root, "html_url");

            // Prefer a Windows installer/zip asset if the release has one.
            string asset = "";
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in assets.EnumerateArray())
                {
                    string name = Str(a, "name");
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        asset = Str(a, "browser_download_url");
                        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { break; }
                    }
                }
            }

            bool newer = IsNewer(latest, current);
            return new Result(newer, current, string.IsNullOrEmpty(latest) ? current : latest, notes, page, asset);
        }
        catch
        {
            return new Result(false, current, current, "", "", "");
        }
    }

    /// <summary>
    /// Download the release asset to the user's Downloads folder and open it. We hand it to the shell
    /// rather than running an installer unattended — the user sees exactly what is about to happen.
    /// </summary>
    public static async Task<string?> DownloadAsync(string assetUrl, IProgress<int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            return null;
        }

        try
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            string file = Path.Combine(downloads, Path.GetFileName(new Uri(assetUrl).LocalPath));

            using (HttpResponseMessage response =
                   await Http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                long? total = response.Content.Headers.ContentLength;
                await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var target = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                    read += n;
                    if (total is > 0)
                    {
                        progress?.Report((int)(read * 100 / total.Value));
                    }
                }
            }

            return file;
        }
        catch
        {
            return null;
        }
    }

    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // Nothing useful to do if the shell won't cooperate.
        }
    }

    public static void OpenPage(string url)
    {
        try
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch
        {
            // Browser unavailable.
        }
    }

    /// <summary>Compare dotted versions numerically, so 1.10.0 beats 1.9.0.</summary>
    private static bool IsNewer(string latest, string current)
    {
        int[] a = Parse(latest), b = Parse(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) { return x > y; }
        }

        return false;
    }

    private static int[] Parse(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) { return new[] { 0 }; }
        string core = v.Split('-')[0];
        var parts = core.Split('.');
        var nums = new List<int>();
        foreach (string p in parts)
        {
            nums.Add(int.TryParse(p, out int n) ? n : 0);
        }

        return nums.ToArray();
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
