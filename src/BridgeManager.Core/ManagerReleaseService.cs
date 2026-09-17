using System.Net.Http.Headers;
using System.Text.Json;
using BridgeManager.Core.FirmwareModules;

namespace BridgeManager.Core;

public sealed record ManagerReleaseUpdate(
    string Version,
    string Name,
    string PageUrl,
    string DownloadUrl,
    string AssetName,
    long AssetSize,
    DateTimeOffset? PublishedAt);

public sealed class ManagerReleaseService
{
    public const string CurrentVersion = "0.3.1-preview.1";
    private const string ReleasesUrl =
        "https://api.github.com/repos/lcyyun/controllerbridge-manger/releases?per_page=30";
    private static readonly HttpClient Client = CreateClient();

    public async Task<ManagerReleaseUpdate?> GetLatestUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ReleasesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream,
            cancellationToken: cancellationToken);
        ManagerReleaseUpdate? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var version = tag.StartsWith('v') ? tag[1..] : tag;
            if (!BridgeModuleVersion.IsValid(version) ||
                BridgeModuleVersion.Compare(version, CurrentVersion) <= 0)
                continue;
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var selected = assets.FirstOrDefault(asset =>
                (asset.GetProperty("name").GetString() ?? "")
                    .EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (selected.ValueKind == JsonValueKind.Undefined)
                selected = assets.FirstOrDefault(asset =>
                    (asset.GetProperty("name").GetString() ?? "")
                        .EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (selected.ValueKind == JsonValueKind.Undefined) continue;
            var download = selected.GetProperty("browser_download_url").GetString() ?? "";
            var page = release.GetProperty("html_url").GetString() ?? "";
            if (!IsGithubUrl(download) || !IsGithubUrl(page)) continue;
            DateTimeOffset? published = null;
            if (DateTimeOffset.TryParse(release.GetProperty("published_at").GetString(),
                    out var parsed)) published = parsed;
            var candidate = new ManagerReleaseUpdate(version,
                release.GetProperty("name").GetString() ?? tag, page, download,
                selected.GetProperty("name").GetString() ?? "download",
                selected.GetProperty("size").GetInt64(), published);
            if (best is null || BridgeModuleVersion.Compare(candidate.Version,
                    best.Version) > 0) best = candidate;
        }
        return best;
    }

    private static bool IsGithubUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ControllerBridge", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
