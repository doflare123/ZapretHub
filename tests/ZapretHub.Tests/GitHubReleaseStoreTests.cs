using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ZapretHub.Core;

namespace ZapretHub.Tests;

public sealed class GitHubReleaseStoreTests
{
    [Fact]
    public async Task DownloadsVerifiedZipPublishesStrategiesAndSkipsInstalledVersion()
    {
        using var directory = new TestDirectory();
        using var origin = new ReleaseOrigin();
        using var client = new HttpClient(origin);
        var store = new GitHubReleaseStore(client, directory.Path);

        Assert.Equal("1.0", await store.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Null(await store.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, origin.MetadataRequests);
        Assert.Equal(1, origin.Downloads);
        var strategy = Assert.Single(store.ListStrategies());
        Assert.Equal(new StrategyId("1.0", "general"), strategy.Id);
        Assert.Equal("@echo off", File.ReadAllText(strategy.ScriptPath));
        Assert.True(File.Exists(Path.Combine(directory.Path, "1.0", "bin", "winws.exe")));
        Assert.Single(Directory.GetDirectories(directory.Path));
    }

    [Fact]
    public async Task BadDigestOrInvalidPackageCannotReplaceCachedVersion()
    {
        using var directory = new TestDirectory();
        using var origin = new ReleaseOrigin();
        using var client = new HttpClient(origin);
        var store = new GitHubReleaseStore(client, directory.Path);
        await store.RefreshAsync(TestContext.Current.CancellationToken);

        origin.Version = "1.1";
        origin.DigestOverride = "sha256:" + new string('3', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RefreshAsync(TestContext.Current.CancellationToken));
        origin.DigestOverride = null;
        origin.Archive = CreateZip("../outside.txt", "bad entry");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RefreshAsync(TestContext.Current.CancellationToken));
        origin.Archive = CreateZip("service.bat", "@echo off");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Equal("1.0", Assert.Single(store.ListStrategies()).Id.Version);
        Assert.Equal("1.0", Path.GetFileName(Assert.Single(Directory.GetDirectories(directory.Path))));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task PruningRetainsNewestVersionsAndTheRollbackVersion()
    {
        using var directory = new TestDirectory();
        using var origin = new ReleaseOrigin();
        using var client = new HttpClient(origin);
        var store = new GitHubReleaseStore(client, directory.Path);
        for (int i = 0; i < 4; i++)
        {
            origin.Version = $"1.{i}";
            await store.RefreshAsync(TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(Path.Combine(directory.Path, origin.Version, ".zaprethub-release"),
                DateTime.UnixEpoch.AddDays(i + 1));
        }

        store.Prune(2, new HashSet<string> { "1.0" });

        Assert.Equal(["1.3", "1.2", "1.0"], store.ListStrategies().Select(strategy => strategy.Id.Version));
    }

    private sealed class ReleaseOrigin : HttpMessageHandler
    {
        public string Version { get; set; } = "1.0";
        public byte[] Archive { get; set; } = CreateZip("bundle/general.bat", "@echo off", "bundle/bin/winws.exe");
        public string? DigestOverride { get; set; }
        public int MetadataRequests { get; private set; }
        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                MetadataRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        tag_name = Version,
                        assets = new[]
                        {
                            new { name = $"zapret-discord-youtube-{Version}.zip", size = Archive.Length,
                                digest = DigestOverride ?? "sha256:" + Convert.ToHexString(SHA256.HashData(Archive)),
                                browser_download_url = $"https://github.com/Flowseal/zapret-discord-youtube/releases/download/{Version}/zapret-discord-youtube-{Version}.zip" }
                        }
                    })
                });
            }
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) });
        }
    }

    private static byte[] CreateZip(string entry, string body, string? secondEntry = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
                writer.Write(body);
            if (secondEntry is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry(secondEntry).Open());
                writer.Write("test artifact");
            }
        }
        return output.ToArray();
    }
}
