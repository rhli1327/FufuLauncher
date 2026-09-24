using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace FufuLauncher.Updates;

public sealed class ReleaseBuild
{
    public int SchemaVersion { get; set; }
    public string BuildId { get; set; } = "";
    public string SourceCommit { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public DateTimeOffset BuiltAtUtc { get; set; }
    public string Channel { get; set; } = "";
    public string Identity => $"{BuildId}:{ContentSha256}";
}

public sealed record PublishedBuild(ReleaseBuild Build, string VersionLabel, string ReleaseUrl,
    string InstallerName, string InstallerUrl, string InstallerSha256);

// Shared by the announcement service and standalone updater. Version labels are display-only.
public sealed class ReleaseUpdateClient(HttpClient httpClient)
{
    public const string Repository = "rhli1327/FufuLauncher";
    public const string ManifestName = "release-build.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ReleaseBuild? ReadInstalledBuild(string directory)
    {
        try { return ParseBuild(File.ReadAllBytes(Path.Combine(directory, ManifestName))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return null; }
    }

    public static bool HasUpdate(ReleaseBuild? installed, ReleaseBuild published) =>
        installed is null || (published.Identity != installed.Identity && published.BuiltAtUtc > installed.BuiltAtUtc);

    public async Task<PublishedBuild?> GetLatestAsync(bool preview = false)
    {
        string endpoint = preview ? "releases?per_page=100" : "releases/latest";
        using var response = await httpClient.GetAsync($"https://api.github.com/repos/{Repository}/{endpoint}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        JsonElement release;
        if (preview)
        {
            var candidates = document.RootElement.EnumerateArray()
                .Where(r => !r.GetProperty("draft").GetBoolean() && r.GetProperty("prerelease").GetBoolean())
                .OrderByDescending(r => r.GetProperty("published_at").GetDateTimeOffset()).ToArray();
            if (candidates.Length == 0) return null;
            release = candidates[0];
        }
        else
        {
            release = document.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("正式版更新接口返回了非正式发布。");
        }

        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var manifest = FindAsset(assets, name => name == ManifestName);
        var installer = FindAsset(assets, name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        string manifestDigest = ReadDigest(manifest);
        string installerDigest = ReadDigest(installer);
        byte[] bytes = await httpClient.GetByteArrayAsync(ReadAssetUrl(manifest));
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifestDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新构建清单的 SHA-256 校验失败。");
        var build = ParseBuild(bytes);
        if (build.Channel != (preview ? "preview" : "stable"))
            throw new InvalidDataException("更新构建清单与发布渠道不一致。");

        string releaseUrl = release.GetProperty("html_url").GetString() ?? "";
        RequireRepositoryUrl(releaseUrl, "tag/");
        return new PublishedBuild(build, release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "",
            releaseUrl, installer.GetProperty("name").GetString()!, ReadAssetUrl(installer), installerDigest);
    }

    private static ReleaseBuild ParseBuild(byte[] bytes)
    {
        var build = JsonSerializer.Deserialize<ReleaseBuild>(bytes, JsonOptions);
        if (build is null || build.SchemaVersion != 1 || string.IsNullOrWhiteSpace(build.BuildId) ||
            build.BuildId.Length > 128 || !IsHex(build.SourceCommit, 40) || !IsHex(build.ContentSha256, 64) ||
            build.BuiltAtUtc == default || build.Channel is not ("stable" or "preview"))
            throw new InvalidDataException("发布包缺少有效的构建标识，请从加固版发布页下载安装包。");
        return build;
    }

    private static JsonElement FindAsset(JsonElement[] assets, Func<string, bool> predicate)
    {
        var matches = assets.Where(a => predicate(a.GetProperty("name").GetString() ?? "")).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("发布包的更新清单或安装包缺失，或存在多个候选文件。");
        return matches[0];
    }

    private static string ReadDigest(JsonElement asset)
    {
        string digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !IsHex(digest[7..], 64))
            throw new InvalidDataException("发布附件缺少有效的 SHA-256，已拒绝更新。");
        return digest[7..];
    }

    private static string ReadAssetUrl(JsonElement asset)
    {
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        RequireRepositoryUrl(url, "download/");
        return url;
    }

    private static void RequireRepositoryUrl(string url, string suffix)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.AbsolutePath.StartsWith($"/{Repository}/releases/{suffix}", StringComparison.Ordinal))
            throw new InvalidDataException("更新地址不属于指定的加固版仓库。");
    }

    private static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(Uri.IsHexDigit);
}
