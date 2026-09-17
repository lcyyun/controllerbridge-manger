using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BridgeManager.Core.FirmwareModules;

public sealed record ManagerUpdate(string Tag, Version Version, bool Prerelease,
    string Name, string Url, long Size, string Sha256);

public sealed class ManagerUpdateService
{
    public const string CurrentTag = "v0.3.1-preview.1";
    public static readonly Version CurrentVersion = new(0, 3, 1);
    private const string Repository = "https://github.com/lcyyun/controllerbridge-manger/";
    private const long MaximumBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    public async Task<ManagerUpdate?> CheckAsync(bool includePrerelease,
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(
            "https://api.github.com/repos/lcyyun/controllerbridge-manger/releases?per_page=100",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return SelectUpdate(json, CurrentVersion, CurrentTag, includePrerelease);
    }

    public static ManagerUpdate? SelectUpdate(string json, Version currentVersion,
        string currentTag, bool includePrerelease)
    {
        using var document = JsonDocument.Parse(json);
        var updates = new List<ManagerUpdate>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var prerelease = release.GetProperty("prerelease").GetBoolean();
            if (prerelease && !includePrerelease) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var match = Regex.Match(tag, @"^v(\d+\.\d+\.\d+)(?:-preview\.(\d+))?$");
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
                continue;
            if (version < currentVersion || tag == currentTag) continue;
            if (version == currentVersion)
            {
                var currentMatch = Regex.Match(currentTag, @"-preview\.(\d+)$");
                if (!currentMatch.Success) continue;
                if (prerelease && (!int.TryParse(match.Groups[2].Value, out var next) ||
                    !int.TryParse(currentMatch.Groups[1].Value, out var current) || next <= current))
                    continue;
            }
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name != $"ControllerBridge-Setup-{version}-win-x64.exe") continue;
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var size = asset.GetProperty("size").GetInt64();
                var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    !Regex.IsMatch(digest[7..], "^[a-fA-F0-9]{64}$") ||
                    url != $"{Repository}releases/download/{tag}/{name}" ||
                    size is <= 0 or > MaximumBytes) continue;
                updates.Add(new(tag, version, prerelease, name, url, size, digest[7..]));
            }
        }
        return updates.OrderByDescending(update => update.Version)
            .ThenBy(update => update.Prerelease)
            .ThenByDescending(update =>
            {
                var match = Regex.Match(update.Tag, @"-preview\.(\d+)$");
                return int.TryParse(match.Groups[1].Value, out var number) ? number : 0;
            }).FirstOrDefault();
    }

    public async Task<string> DownloadAsync(ManagerUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(update.Tag, @"^v\d+\.\d+\.\d+(?:-preview\.\d+)?$") ||
            update.Name != $"ControllerBridge-Setup-{update.Version}-win-x64.exe" ||
            update.Url != $"{Repository}releases/download/{update.Tag}/{update.Name}" ||
            !Regex.IsMatch(update.Sha256, "^[a-fA-F0-9]{64}$") ||
            update.Size is <= 0 or > MaximumBytes)
            throw new InvalidDataException("Invalid manager update asset.");
        var directory = Path.Combine(Path.GetTempPath(), "ControllerBridge-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.Name);
        try
        {
            using var response = await Client.GetAsync(update.Url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != update.Size)
                throw new InvalidDataException("Update size mismatch.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > update.Size) throw new InvalidDataException("Update exceeded expected size.");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (total != update.Size ||
                    !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update integrity check failed.");
            }
            return path;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ControllerBridge", "0.2.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
