using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FufuLauncher.Updates;

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
    passed++;
}
async Task Reject(FakeReleaseServer server, string description)
{
    try { await new ReleaseUpdateClient(new HttpClient(server)).GetLatestAsync(); }
    catch (InvalidDataException) { Check(true, description); return; }
    throw new Exception($"FAIL: {description}");
}

var installed = new ReleaseBuild
{
    SchemaVersion = 1, BuildId = "original-build", SourceCommit = new string('a', 40),
    ContentSha256 = new string('b', 64), BuiltAtUtc = DateTimeOffset.Parse("2026-09-24T00:00:00Z"), Channel = "stable"
};
using var server = new FakeReleaseServer();
var client = new ReleaseUpdateClient(new HttpClient(server));
var release = (await client.GetLatestAsync())!;
Check(release.VersionLabel == "1.7.0.0" && ReleaseUpdateClient.HasUpdate(installed, release.Build),
    "A rebuilt 1.7.0.0 updates an older 1.7.0.0 without changing the version label");
Check(!ReleaseUpdateClient.HasUpdate(release.Build, release.Build), "The installed build does not prompt again");
Check(ReleaseUpdateClient.HasUpdate(null, release.Build), "An installation without build metadata can upgrade");
Check(!ReleaseUpdateClient.HasUpdate(release.Build, installed), "An older published build does not cause a downgrade");
Check(release.InstallerSha256 == new string('c', 64), "Installer SHA-256 is preserved for download verification");

server.Tag = "v99.0.0.0";
var relabelled = (await client.GetLatestAsync())!;
Check(!ReleaseUpdateClient.HasUpdate(release.Build, relabelled.Build), "Changing only a version label does not trigger an update");
server.Tag = "upstream-label";
Check((await client.GetLatestAsync())!.VersionLabel == "upstream-label", "Update detection does not require a numeric version");

server.Preview = true;
var preview = (await client.GetLatestAsync(preview: true))!;
Check(preview.Build.Channel == "preview" && ReleaseUpdateClient.HasUpdate(installed, preview.Build),
    "Preview selection uses release channel and build identity");
server.NoReleases = true;
Check(await client.GetLatestAsync(preview: true) == null, "An empty preview channel has no update");
server.NoReleases = false;
server.Preview = false;
server.NotFound = true;
Check(await client.GetLatestAsync() == null, "A missing stable release has no update");

await Reject(new FakeReleaseServer { TamperManifest = true }, "A manifest with the wrong SHA-256 is rejected");
await Reject(new FakeReleaseServer { OmitManifest = true }, "A release without build metadata is rejected");
await Reject(new FakeReleaseServer { InstallerDigest = "" }, "An installer without a digest is rejected");
await Reject(new FakeReleaseServer { AssetHost = "http://github.com" }, "An HTTP download URL is rejected");
await Reject(new FakeReleaseServer { AssetHost = "https://example.com" }, "An unrelated download host is rejected");
await Reject(new FakeReleaseServer { WrongChannel = true }, "A manifest from the wrong channel is rejected");
await Reject(new FakeReleaseServer { Preview = true }, "The stable endpoint cannot silently select a preview");
await Reject(new FakeReleaseServer { InvalidBuild = true }, "Malformed build metadata is rejected");

string fixture = Path.Combine(AppContext.BaseDirectory, "installed-fixture");
Directory.CreateDirectory(fixture);
string metadata = Path.Combine(fixture, ReleaseUpdateClient.ManifestName);
try
{
    File.WriteAllBytes(metadata, JsonSerializer.SerializeToUtf8Bytes(release.Build));
    Check(ReleaseUpdateClient.ReadInstalledBuild(fixture)?.Identity == release.Build.Identity,
        "Installed metadata survives a round trip without requiring the installer digest");
    File.WriteAllText(metadata, "{}");
    Check(ReleaseUpdateClient.ReadInstalledBuild(fixture) == null, "Invalid local metadata permits repair");
    File.WriteAllText(metadata, "{\"schemaVersion\":1,\"buildId\":\"x\",\"sourceCommit\":null}");
    Check(ReleaseUpdateClient.ReadInstalledBuild(fixture) == null, "Null local fields do not crash update checks");
}
finally { File.Delete(metadata); Directory.Delete(fixture); }
Console.WriteLine($"{passed} release update checks passed.");

sealed class FakeReleaseServer : HttpMessageHandler
{
    public string Tag = "v1.7.0.0";
    public string InstallerDigest = "sha256:" + new string('c', 64);
    public string AssetHost = "https://github.com";
    public bool Preview, NoReleases, NotFound, TamperManifest, OmitManifest, WrongChannel, InvalidBuild;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var build = new ReleaseBuild
        {
            SchemaVersion = 1, BuildId = "repaired-build", SourceCommit = new string('d', 40),
            ContentSha256 = new string('e', 64), BuiltAtUtc = DateTimeOffset.Parse("2026-09-24T01:00:00Z"),
            Channel = Preview || WrongChannel ? "preview" : "stable"
        };
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(build);
        if (InvalidBuild) manifest = "{}"u8.ToArray();
        if (request.RequestUri!.Host == "api.github.com")
        {
            if (NotFound) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var assets = new List<object>
            {
                new { name = "FufuLauncher.Hardened_Setup_v1.7.0.0.exe", digest = InstallerDigest,
                    browser_download_url = $"{AssetHost}/{ReleaseUpdateClient.Repository}/releases/download/{Tag}/setup.exe" }
            };
            if (!OmitManifest) assets.Add(new
            {
                name = ReleaseUpdateClient.ManifestName,
                digest = "sha256:" + Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant(),
                browser_download_url = $"{AssetHost}/{ReleaseUpdateClient.Repository}/releases/download/{Tag}/{ReleaseUpdateClient.ManifestName}"
            });
            object release = new
            {
                draft = false, prerelease = Preview, tag_name = Tag, published_at = "2026-09-24T02:00:00Z",
                html_url = $"https://github.com/{ReleaseUpdateClient.Repository}/releases/tag/{Tag}", assets
            };
            byte[] body = request.RequestUri.Query.Length > 0
                ? JsonSerializer.SerializeToUtf8Bytes(NoReleases ? Array.Empty<object>() : new[] { release })
                : JsonSerializer.SerializeToUtf8Bytes(release);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
        if (TamperManifest) manifest = "tampered"u8.ToArray();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) });
    }
}
