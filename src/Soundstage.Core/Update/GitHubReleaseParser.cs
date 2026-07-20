using System.Text.Json;

namespace Soundstage.Core.Update;

/// <summary>
/// Parses the JSON body of GitHub's <c>releases/latest</c> endpoint into an
/// <see cref="UpdateInfo"/>. Pure and defensive — a shape it doesn't recognize yields null
/// rather than throwing, so a GitHub API change can never crash the updater.
/// </summary>
public static class GitHubReleaseParser
{
    public static UpdateInfo? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Skip drafts (never offered as updates).
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (!UpdatePolicy.TryParseTag(tag, out var version))
            {
                return null;
            }

            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag! : tag!;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assetArray.EnumerateArray())
                {
                    var assetName = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(assetName) || string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                    var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                        ? digest["sha256:".Length..]
                        : null;

                    assets.Add(new ReleaseAsset(assetName!, url!, size, sha256));
                }
            }

            return new UpdateInfo(version, tag!, name, notes, htmlUrl, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
