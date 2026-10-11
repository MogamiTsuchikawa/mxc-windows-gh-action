using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mxc.GitHubRunner.GitHub;

namespace Mxc.GitHubRunner.Tests;

public sealed class GitHubRunnerClientTests
{
    private const string Version = "2.338.0";
    private const string FileName = "actions-runner-win-x64-2.338.0.zip";
    private static readonly Uri AssetUrl = new($"https://github.com/actions/runner/releases/download/v{Version}/{FileName}");

    [Fact]
    public async Task ResolvePinnedReleaseUsesExactAssetAndDigest()
    {
        using var http = Client(request =>
        {
            Assert.Equal("https://api.github.com/repos/actions/runner/releases/tags/v2.338.0", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Json(new { tag_name = "v" + Version, assets = new[] { new { name = FileName, browser_download_url = AssetUrl.AbsoluteUri, digest = "sha256:" + new string('A', 64) } } });
        });
        var release = await new GitHubRunnerClient(http).ResolveReleaseAsync("v" + Version, "x64");
        Assert.Equal(Version, release.Version);
        Assert.Equal(new string('a', 64), release.Sha256);
        Assert.Equal(AssetUrl, release.DownloadUrl);
    }

    [Fact]
    public async Task ResolveLatestSupportsOfficialReleaseBodyChecksum()
    {
        using var http = Client(request =>
        {
            Assert.EndsWith("/releases/latest", request.RequestUri!.AbsoluteUri);
            return ReleaseJson(digest: null, body: $"<!-- BEGIN SHA win-x64 -->{new string('b', 64)}<!-- END SHA win-x64 -->");
        });
        Assert.Equal(new string('b', 64), (await new GitHubRunnerClient(http).ResolveReleaseAsync("latest", "x64")).Sha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256:invalid")]
    [InlineData("sha512:abcd")]
    public async Task ResolveReleaseRejectsMissingOrInvalidChecksum(string? digest)
    {
        using var http = Client(_ => ReleaseJson(digest));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRunnerClient(http).ResolveReleaseAsync(Version, "x64"));
    }

    [Fact]
    public async Task ResolveReleaseRejectsUnexpectedAssetOrigin()
    {
        using var http = Client(_ => Json(new { tag_name = "v" + Version, assets = new[] { new { name = FileName, browser_download_url = "https://example.com/runner.zip", digest = "sha256:" + new string('a', 64) } } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRunnerClient(http).ResolveReleaseAsync(Version, "x64"));
    }

    [Fact]
    public async Task DownloadPublishesVerifiedPackageWithoutAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Archive(("bin/Runner.Listener.exe", "runner"), ("externals/node/file.txt", "node"));
        using var http = Client(request =>
        {
            Assert.Equal(AssetUrl, request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var target = Path.Combine(directory.Path, "runner");
        await new GitHubRunnerClient(http).DownloadAndExtractAsync(Release(bytes), target);
        Assert.Equal("runner", await File.ReadAllTextAsync(Path.Combine(target, "bin", "Runner.Listener.exe")));
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ChecksumMismatchLeavesNoPackageOrTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Archive(("bin/Runner.Listener.exe", "runner"));
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRunnerClient(http).DownloadAndExtractAsync(Release(bytes) with { Sha256 = new string('0', 64) }, Path.Combine(directory.Path, "runner")));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    [InlineData("C:/escaped.txt")]
    [InlineData("/escaped.txt")]
    [InlineData("bin/file:stream")]
    [InlineData("bin/NUL.txt")]
    public async Task UnsafeZipPathsAreRejectedAndCleaned(string entry)
    {
        using var directory = new TemporaryDirectory();
        var bytes = Archive(("bin/Runner.Listener.exe", "runner"), (entry, "unsafe"));
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRunnerClient(http).DownloadAndExtractAsync(Release(bytes), Path.Combine(directory.Path, "runner")));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ZipSymbolicLinksAreRejected()
    {
        using var directory = new TemporaryDirectory();
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("../../outside");
        }
        var bytes = memory.ToArray();
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRunnerClient(http).DownloadAndExtractAsync(Release(bytes), Path.Combine(directory.Path, "runner")));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ExistingDestinationIsNeverOverwritten()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "runner");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "keep");
        using var http = Client(_ => throw new InvalidOperationException("Must not download"));
        await Assert.ThrowsAsync<IOException>(() => new GitHubRunnerClient(http).DownloadAndExtractAsync(Release([]), path));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(path, "keep.txt")));
    }

    [Fact]
    public async Task CancellationCleansDownload()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        using var http = Client(_ =>
        {
            cancellation.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) };
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubRunnerClient(http).DownloadAndExtractAsync(Release(new byte[100]), Path.Combine(directory.Path, "runner"), cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task RegistrationTokenIsScopedToApiAndSafeToFormat()
    {
        using var http = Client(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.github.com/repos/owner/repo/actions/runners/registration-token", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer secret-pat", request.Headers.Authorization!.ToString());
            return Json(new { token = "secret-registration", expires_at = "2026-10-11T04:00:00Z" });
        });
        var registration = await new GitHubRunnerClient(http).CreateRegistrationTokenAsync(new Uri("https://github.com/owner/repo"), "secret-pat");
        Assert.Equal("secret-registration", registration.Token);
        Assert.DoesNotContain("secret-registration", registration.ToString());
    }

    [Fact]
    public async Task RemovalPaginatesAndDeletesOnlyExactName()
    {
        var paths = new List<string>();
        using var http = Client(request =>
        {
            var uri = request.RequestUri!;
            paths.Add(uri.PathAndQuery);
            Assert.Equal("Bearer secret-pat", request.Headers.Authorization!.ToString());
            if (request.Method == HttpMethod.Delete)
            {
                Assert.EndsWith("/runners/1001", uri.AbsoluteUri);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (uri.Query.EndsWith("&page=1", StringComparison.Ordinal))
                return Json(new { runners = Enumerable.Range(1, 100).Select(i => new { id = i, name = "unrelated-" + i }) });
            return Json(new { runners = new[] { new { id = 1000, name = "runner-suffix" }, new { id = 1001, name = "runner" } } });
        });
        Assert.True(await new GitHubRunnerClient(http).RemoveRunnerAsync(new Uri("https://github.com/owner/repo"), "runner", "secret-pat"));
        Assert.Equal(3, paths.Count);
        Assert.Contains("page=2", paths[1]);
    }

    [Fact]
    public async Task DuplicateNamesPreventDeletion()
    {
        using var http = Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(new { runners = new[] { new { id = 1, name = "runner" }, new { id = 2, name = "runner" } } });
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRunnerClient(http).RemoveRunnerAsync(new Uri("https://github.com/owner/repo"), "runner", "secret-pat"));
    }

    [Fact]
    public async Task HttpErrorsDoNotReflectSecretsInResponseBody()
    {
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret-pat secret-registration") });
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubRunnerClient(http).CreateRegistrationTokenAsync(new Uri("https://github.com/owner/repo"), "secret-pat"));
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public void DefaultAuthorizationHeadersAreRejected()
    {
        using var http = Client(_ => throw new InvalidOperationException());
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-pat");
        Assert.Throws<ArgumentException>(() => new GitHubRunnerClient(http));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new FakeHandler(respond));
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage ReleaseJson(string? digest, string? body = null) => Json(new { tag_name = "v" + Version, body, assets = new[] { new { name = FileName, browser_download_url = AssetUrl.AbsoluteUri, digest } } });
    private static RunnerRelease Release(byte[] bytes) => new(Version, "x64", AssetUrl, Convert.ToHexStringLower(SHA256.HashData(bytes)), FileName);

    private static byte[] Archive(params (string Path, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        return memory.ToArray();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mxc-github-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
