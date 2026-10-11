using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mxc.GitHubRunner.Configuration;
using Mxc.GitHubRunner.GitHub;
using Mxc.GitHubRunner.Isolation;
using Mxc.GitHubRunner.Runtime;

namespace Mxc.GitHubRunner.Tests;

[CollectionDefinition("Runner lifecycle", DisableParallelization = true)]
public sealed class RunnerLifecycleCollection;

[Collection("Runner lifecycle")]
public sealed class GitHubRunnerServiceTests
{
    [Fact]
    public async Task ReadableStateTreeIsRejectedBeforeDownloadingOrRegistering()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService(new FakeExecutor(_ => throw new InvalidOperationException("Must not spawn")));
        var unsafeConfiguration = fixture.Configuration with { StateDirectory = Path.Combine(AppContext.BaseDirectory, "state") };
        await Assert.ThrowsAsync<ConfigurationException>(() => service.PrepareAsync(unsafeConfiguration, CancellationToken.None));
        if (OperatingSystem.IsWindows())
            await Assert.ThrowsAsync<ConfigurationException>(() => service.RunAsync(unsafeConfiguration, CancellationToken.None));
        Assert.Empty(fixture.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task UnavailableMxcFailsBeforeAnyNetworkOrRegistration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var service = fixture.CreateService(new FakeExecutor(_ => throw new InvalidOperationException("Must not spawn")),
            () => new MxcHostInfo(false, "Native runtime unavailable.", null, []));
        var error = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => service.RunAsync(fixture.Configuration, CancellationToken.None));
        Assert.Equal("Native runtime unavailable.", error.Message);
        Assert.Empty(fixture.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task PrepareCacheRoundTripsUriMetadataAndAvoidsSecondDownload()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService(new FakeExecutor(_ => throw new InvalidOperationException()));
        var first = await service.PrepareAsync(fixture.Configuration, CancellationToken.None);
        var second = await service.PrepareAsync(fixture.Configuration, CancellationToken.None);
        Assert.Equal(first, second);
        Assert.Equal(1, fixture.Requests.Count(request => request.Host == "github.com"));
        Assert.Equal(2, fixture.Requests.Count(request => request.AbsolutePath.EndsWith("/releases/latest")));
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 1)]
    [InlineData(5, false, 5)]
    [InlineData(1, true, 124)]
    public async Task CompletedFailedAndTimedOutWorkersCleanTheirCloneAndRegistration(int exitCode, bool timedOut, int expectedExit)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.SetCredentials();
        var executor = new FakeExecutor(spec =>
        {
            var bootstrap = JsonSerializer.Deserialize<WorkerRequest>(spec.StandardInput!)!;
            fixture.RunnerName = bootstrap.Name;
            Assert.Equal(Fixture.RegistrationToken, bootstrap.RegistrationToken);
            Assert.DoesNotContain(Fixture.AccessToken, spec.StandardInput!);
            Assert.DoesNotContain(Fixture.AccessToken, spec.Command);
            Assert.DoesNotContain(Fixture.RegistrationToken, spec.Command);
            Assert.DoesNotContain(spec.Environment.Values, value => value.Contains(Fixture.AccessToken) || value.Contains(Fixture.RegistrationToken));
            Assert.DoesNotContain("MXC_GITHUB_TOKEN", spec.Environment.Keys);
            Assert.DoesNotContain("MXC_RUNNER_REGISTRATION_TOKEN", spec.Environment.Keys);
            Assert.True(File.Exists(Path.Combine(bootstrap.RunnerDirectory, "bin", "Runner.Listener.exe")));
            foreach (var file in Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories))
            {
                using var read = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var text = read.ReadToEnd();
                Assert.DoesNotContain(Fixture.AccessToken, text);
                Assert.DoesNotContain(Fixture.RegistrationToken, text);
            }
            return Task.FromResult(new MxcExecutionResult(exitCode, timedOut));
        });
        var actual = await fixture.CreateService(executor).RunAsync(fixture.Configuration, CancellationToken.None);
        Assert.Equal(expectedExit, actual);
        Assert.True(fixture.Removed);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.Root, "runs")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "packages", "2.338.0-" + fixture.Configuration.Architecture, "bin", "Runner.Listener.exe")));
        var log = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "logs"))));
        Assert.DoesNotContain(Fixture.AccessToken, log);
        Assert.DoesNotContain(Fixture.RegistrationToken, log);
        Assert.Contains("first part\nsecond line\n", log);
        Assert.DoesNotContain("first " + Environment.NewLine + "part", log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndSpawnFailureStillCleanUsingIndependentToken(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.SetCredentials();
        var executor = new FakeExecutor(spec =>
        {
            fixture.RunnerName = JsonSerializer.Deserialize<WorkerRequest>(spec.StandardInput!)!.Name;
            if (cancel)
            {
                cancellation.Cancel();
                return Task.FromCanceled<MxcExecutionResult>(cancellation.Token);
            }
            return Task.FromException<MxcExecutionResult>(new IOException("Native spawn failed."));
        });
        var running = fixture.CreateService(executor).RunAsync(fixture.Configuration, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        else await Assert.ThrowsAsync<IOException>(() => running);
        Assert.True(fixture.Removed);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.Root, "runs")));
    }

    [Fact]
    public async Task BrokenOutputCannotPreventDeletingRunnerCredentialsDuringCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.SetRegistrationCredentials();
        var originalOutput = Console.Out;
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => fixture.CreateService(new BrokenOutputExecutor())
                .RunAsync(fixture.Configuration, CancellationToken.None));
            Assert.Equal("output stream unavailable", error.Message);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.Root, "runs")));
        }
        finally { Console.SetOut(originalOutput); }
    }

    private sealed class BrokenOutputExecutor : IMxcExecutor
    {
        public Task<MxcExecutionResult> ExecuteAsync(MxcExecutionSpec spec, Action<string> stdout, Action<string> stderr,
            CancellationToken cancellationToken)
        {
            var bootstrap = JsonSerializer.Deserialize<WorkerRequest>(spec.StandardInput!)!;
            File.WriteAllText(Path.Combine(bootstrap.RunnerDirectory, ".credentials"), "runner credential fixture");
            Console.SetOut(new BrokenWriter());
            stdout("workload output");
            throw new InvalidOperationException("The broken output sink should have thrown.");
        }
    }

    private sealed class BrokenWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) => throw new IOException("output stream unavailable");
        public override void WriteLine(string? value) => throw new IOException("output stream unavailable");
    }

    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(0, false, true, true)]
    [InlineData(1, false, false, true)]
    [InlineData(0, true, false, true)]
    public async Task RegistrationTokenCleanupWarningOnlyDisappearsAfterSuccessfulEphemeralCleanup(
        int exitCode, bool timedOut, bool configurationRemains, bool expectedWarning)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.SetRegistrationCredentials();
        var executor = new FakeExecutor(spec =>
        {
            if (configurationRemains)
            {
                var bootstrap = JsonSerializer.Deserialize<WorkerRequest>(spec.StandardInput!)!;
                File.WriteAllText(Path.Combine(bootstrap.RunnerDirectory, ".runner"), "registered");
            }
            return Task.FromResult(new MxcExecutionResult(exitCode, timedOut));
        });
        await fixture.CreateService(executor).RunAsync(fixture.Configuration, CancellationToken.None);
        var log = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "logs"))));
        Assert.Equal(expectedWarning, log.Contains("may remain offline", StringComparison.Ordinal));
        Assert.False(fixture.Removed);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.Root, "runs")));
    }

    private sealed class FakeExecutor(Func<MxcExecutionSpec, Task<MxcExecutionResult>> execute) : IMxcExecutor
    {
        public Task<MxcExecutionResult> ExecuteAsync(MxcExecutionSpec spec, Action<string> stdout, Action<string> stderr, CancellationToken cancellationToken)
        {
            stdout("first ");
            stdout("part\nsecond line\n");
            stderr("redacted ***\n");
            return execute(spec);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const string AccessToken = "test-host-only-access-token";
        public const string RegistrationToken = "test-bootstrap-registration-token";
        private readonly string? originalAccessToken = Environment.GetEnvironmentVariable("MXC_GITHUB_TOKEN");
        private readonly string? originalRegistrationToken = Environment.GetEnvironmentVariable("MXC_RUNNER_REGISTRATION_TOKEN");
        private readonly HttpClient http;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mxc-service-test-" + Guid.NewGuid().ToString("N"));
        public RunnerConfiguration Configuration { get; }
        public List<Uri> Requests { get; } = [];
        public string? RunnerName { get; set; }
        public bool Removed { get; private set; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Configuration = new RunnerConfiguration { Repository = "https://github.com/test-owner/test-repo", StateDirectory = Root,
                Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() };
            var bytes = Package();
            var fileName = $"actions-runner-win-{Configuration.Architecture}-2.338.0.zip";
            http = new HttpClient(new FakeHandler(request =>
            {
                var uri = request.RequestUri!;
                Requests.Add(uri);
                if (uri.AbsolutePath.EndsWith("/releases/latest"))
                    return Json(new { tag_name = "v2.338.0", assets = new[] { new { name = fileName,
                        browser_download_url = "https://github.com/actions/runner/releases/download/v2.338.0/" + fileName,
                        digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) } } });
                if (uri.Host == "github.com")
                {
                    Assert.Null(request.Headers.Authorization);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                }
                Assert.Equal("Bearer " + AccessToken, request.Headers.Authorization!.ToString());
                if (uri.AbsolutePath.EndsWith("/registration-token"))
                    return Json(new { token = RegistrationToken, expires_at = "2099-01-01T00:00:00Z" });
                if (request.Method == HttpMethod.Get)
                    return Json(new { runners = new[] { new { id = 42, name = RunnerName } } });
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.EndsWith("/runners/42", uri.AbsoluteUri);
                Removed = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
        }

        public void SetCredentials()
        {
            Environment.SetEnvironmentVariable("MXC_GITHUB_TOKEN", AccessToken);
            Environment.SetEnvironmentVariable("MXC_RUNNER_REGISTRATION_TOKEN", null);
        }

        public void SetRegistrationCredentials()
        {
            Environment.SetEnvironmentVariable("MXC_GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("MXC_RUNNER_REGISTRATION_TOKEN", RegistrationToken);
        }

        public RunnerService CreateService(IMxcExecutor executor, Func<MxcHostInfo>? probe = null) =>
            new(new GitHubRunnerClient(http), executor, probe ?? (() => new MxcHostInfo(true, null, "test", [])));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MXC_GITHUB_TOKEN", originalAccessToken);
            Environment.SetEnvironmentVariable("MXC_RUNNER_REGISTRATION_TOKEN", originalRegistrationToken);
            http.Dispose();
            Directory.Delete(Root, recursive: true);
        }

        private static byte[] Package()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("bin/Runner.Listener.exe").Open());
                writer.Write("test runner executable");
            }
            return stream.ToArray();
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
