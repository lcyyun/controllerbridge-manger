using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;

namespace BridgeManager.Core.FirmwareModules;

public sealed record GithubModuleAsset(
    string Name,
    string DownloadUrl,
    string Tag,
    long Size,
    DateTimeOffset? PublishedAt,
    string? Sha256 = null)
{
    public string DisplayName => Path.GetFileNameWithoutExtension(Name);
    public string Detail =>
        $"{Tag} · {Math.Max(1, Size / 1024)} KB";
}

public sealed class GithubModuleReleaseService
{
    public const string DefaultOwner = "lcyyun";
    public const string DefaultRepository = "controllerbridge-manger";
    private const long MaximumPackageBytes = 512L * 1024L * 1024L;
    private static readonly HttpClient Client = CreateClient();

    public async Task<IReadOnlyList<GithubModuleAsset>> GetModuleAssetsAsync(
        string owner = DefaultOwner,
        string repository = DefaultRepository,
        CancellationToken cancellationToken = default,
        bool aggregateOfficialRepositories = true)
    {
        ValidateRepositoryPart(owner);
        ValidateRepositoryPart(repository);
        if (aggregateOfficialRepositories && owner == DefaultOwner && repository == DefaultRepository)
        {
            var official = await Task.WhenAll(new[]
            {
                "controllerbridge-SF32LB52", "controllerbridge-pico2w",
                "controllerbridge-esp32s3"
            }.Select(repo => GetModuleAssetsAsync(owner, repo, cancellationToken)));
            return official.SelectMany(items => items)
                .OrderByDescending(asset => asset.PublishedAt).ToArray();
        }
        var assets = new List<GithubModuleAsset>();
        for (var page = 1; ; page++)
        {
            var url = $"https://api.github.com/repos/{owner}/{repository}/releases" +
                      $"?per_page=100&page={page}";
            using var response = await Client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream,
                cancellationToken: cancellationToken);
            var releaseCount = document.RootElement.GetArrayLength();
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) &&
                    draft.ValueKind == JsonValueKind.True)
                {
                    continue;
                }
                var tag = release.GetProperty("tag_name").GetString() ?? "release";
                DateTimeOffset? publishedAt = null;
                if (release.TryGetProperty("published_at", out var published) &&
                    DateTimeOffset.TryParse(published.GetString(), out var parsed))
                {
                    publishedAt = parsed;
                }
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(".cbmodule",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var downloadUrl = asset.GetProperty("browser_download_url")
                        .GetString() ?? "";
                    var size = asset.GetProperty("size").GetInt64();
                    if (size is <= 0 or > MaximumPackageBytes ||
                        !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                        uri.Scheme != Uri.UriSchemeHttps ||
                        !uri.AbsolutePath.StartsWith($"/{owner}/{repository}/releases/download/", StringComparison.Ordinal) ||
                        !uri.Host.Equals("github.com",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var digest = asset.TryGetProperty("digest", out var digestElement)
                        ? digestElement.GetString() : null;
                    var sha256 = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true
                        ? digest[7..] : null;
                    if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                        continue;
                    assets.Add(new GithubModuleAsset(
                        name, downloadUrl, tag, size, publishedAt, sha256));
                }
            }
            if (releaseCount < 100)
            {
                break;
            }
        }
        return assets
            .OrderByDescending(asset => asset.PublishedAt)
            .ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> DownloadAsync(
        GithubModuleAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(asset.DownloadUrl);
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            asset.Sha256 is null || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit) ||
            asset.Size is <= 0 or > MaximumPackageBytes)
        {
            throw new InvalidDataException("GitHub module asset is invalid.");
        }

        using var response = await Client.GetAsync(uri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? asset.Size;
        if (length is <= 0 or > MaximumPackageBytes)
        {
            throw new InvalidDataException("GitHub module package is too large.");
        }

        var path = Path.Combine(Path.GetTempPath(),
            $"controller-bridge-{Guid.NewGuid():N}.cbmodule");
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            await using var target = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                written += count;
                if (written > MaximumPackageBytes)
                {
                    throw new InvalidDataException(
                        "GitHub module package exceeded the size limit.");
                }
                await target.WriteAsync(buffer.AsMemory(0, count),
                    cancellationToken);
                hash.AppendData(buffer, 0, count);
                progress?.Report(Math.Clamp((double)written / length, 0, 1));
            }
            await target.FlushAsync(cancellationToken);
            if (asset.Size > 0 && written != asset.Size)
            {
                throw new InvalidDataException(
                    $"GitHub module size mismatch: expected {asset.Size}, got {written}.");
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub module SHA256 mismatch.");
            return path;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ControllerBridge", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static void ValidateRepositoryPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) ||
                              ch is '-' or '_' or '.')))
        {
            throw new ArgumentException("GitHub repository name is invalid.");
        }
    }
}
