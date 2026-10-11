using System.Runtime.InteropServices;
using System.Text.Json;
using Mxc.GitHubRunner.Configuration;
using Mxc.GitHubRunner.GitHub;
using Mxc.GitHubRunner.Isolation;

namespace Mxc.GitHubRunner.Runtime;

public sealed class RunnerService(GitHubRunnerClient github, IMxcExecutor executor,
    Func<MxcHostInfo>? probeHost = null)
{
    public async Task<string> PrepareAsync(RunnerConfiguration configuration, CancellationToken cancellationToken)
    {
        ExecutionEnvironment.ValidateStateDirectory(configuration);
        OwnedDirectory.AssertNoReparsePoints(configuration.StateDirectory);
        var release = await github.ResolveReleaseAsync(configuration.RunnerVersion, configuration.Architecture, cancellationToken);
        var package = Path.Combine(configuration.StateDirectory, "packages", $"{release.Version}-{release.Architecture}");
        OwnedDirectory.AssertNoReparsePoints(package);
        var marker = Path.Combine(package, "mxc-package.json");
        if (Directory.Exists(package))
        {
            if (!File.Exists(marker) || !File.Exists(Path.Combine(package, "bin", "Runner.Listener.exe")))
                throw new IOException($"Incomplete package directory. Remove it before retrying: {package}");
            var saved = JsonSerializer.Deserialize<RunnerRelease>(await File.ReadAllTextAsync(marker, cancellationToken));
            if (saved != release) throw new IOException("Cached runner metadata does not match the verified GitHub release.");
        }
        else
        {
            Console.WriteLine($"Downloading and verifying GitHub runner {release.Version} (win-{release.Architecture}).");
            await github.DownloadAndExtractAsync(release, package, cancellationToken);
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(release), cancellationToken);
        }
        if (File.Exists(Path.Combine(package, ".runner")) || File.Exists(Path.Combine(package, ".credentials")))
            throw new IOException("The package template must not contain a registered runner.");
        Console.WriteLine($"Runner package: {package}");
        return package;
    }

    public async Task<int> RunAsync(RunnerConfiguration configuration, CancellationToken cancellationToken)
    {
        configuration.ValidateForRun();
        RequireWindowsArchitecture(configuration);
        ExecutionEnvironment.ValidateStateDirectory(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        var support = (probeHost ?? MxcExecutor.ProbeHost)();
        if (!support.IsSupported)
            throw new PlatformNotSupportedException(support.Reason ?? "MXC ProcessContainer is unavailable on this host.");
        foreach (var warning in support.Warnings) Console.Error.WriteLine("MXC: " + warning);
        var token = Environment.GetEnvironmentVariable("MXC_RUNNER_REGISTRATION_TOKEN");
        var accessToken = Environment.GetEnvironmentVariable("MXC_GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Set MXC_RUNNER_REGISTRATION_TOKEN, or MXC_GITHUB_TOKEN with repository administration permission.");
        var package = await PrepareAsync(configuration, cancellationToken);
        var repository = new Uri(configuration.Repository);
        if (string.IsNullOrWhiteSpace(token))
            token = (await github.CreateRegistrationTokenAsync(repository, accessToken!, cancellationToken)).Token;
        var id = Guid.NewGuid().ToString("N");
        var name = $"{configuration.NamePrefix}-{id[..12]}";
        var runs = Path.Combine(configuration.StateDirectory, "runs");
        var runDirectory = Path.Combine(runs, id);
        var logs = Path.Combine(configuration.StateDirectory, "logs");
        OwnedDirectory.AssertNoReparsePoints(runs);
        OwnedDirectory.AssertNoReparsePoints(logs);
        Directory.CreateDirectory(logs);
        using var log = new StreamWriter(new FileStream(Path.Combine(logs, $"{name}.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var outputLock = new object();
        void Write(string text, bool error = false, bool appendNewLine = true)
        {
            foreach (var secret in new[] { token, accessToken })
                if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "***", StringComparison.Ordinal);
            lock (outputLock)
            {
                var destination = error ? Console.Error : Console.Out;
                if (appendNewLine)
                {
                    destination.WriteLine(text);
                    log.WriteLine(text);
                }
                else
                {
                    destination.Write(text);
                    log.Write(text);
                }
            }
        }
        void WriteCleanupDiagnostic(string text, bool error = false)
        {
            // Cleanup must still run when the original failure was a closed
            // console pipe or an unwritable log volume.
            try { Write(text, error); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        var attempted = false;
        var ephemeralCleanupCompleted = false;
        try
        {
            var runner = Path.Combine(runDirectory, "runner");
            OwnedDirectory.CopyTree(package, runner, cancellationToken);
            var work = Path.Combine(runDirectory, "work");
            Directory.CreateDirectory(work);
            var bootstrap = new WorkerRequest(configuration.Repository, name, configuration.Labels, runner, work, token!);
            var spec = ExecutionEnvironment.Create(configuration, runDirectory,
                WindowsCommandLine.CurrentApplication("__worker"), JsonSerializer.Serialize(bootstrap) + "\n")
                with { SensitiveValues = new[] { token!, accessToken ?? "" } };
            Write($"Starting ephemeral runner {name} in MXC. Labels: {string.Join(", ", configuration.Labels)}");
            attempted = true;
            var result = await executor.ExecuteAsync(spec, text => Write(text, appendNewLine: false),
                text => Write(text, error: true, appendNewLine: false), cancellationToken);
            ephemeralCleanupCompleted = result.ExitCode == 0 && !result.TimedOut
                && !File.Exists(Path.Combine(runner, ".runner"))
                && !File.Exists(Path.Combine(runner, ".credentials"));
            if (result.TimedOut) { Write("Runner lifetime limit reached.", true); return 124; }
            Write($"Runner process exited with code {result.ExitCode}. Check the job result on GitHub Actions.");
            return result.ExitCode;
        }
        finally
        {
            try
            {
                if (attempted && !string.IsNullOrWhiteSpace(accessToken))
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    try { await github.RemoveRunnerAsync(repository, name, accessToken, cleanupTimeout.Token); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        WriteCleanupDiagnostic($"Remote cleanup failed ({exception.GetType().Name}). Remove runner '{name}' in the repository's Actions settings if it remains registered.", true);
                    }
                }
                else if (attempted && !ephemeralCleanupCompleted)
                    WriteCleanupDiagnostic($"With registration-token authentication, a runner stopped before completing a job may remain offline. If needed, remove '{name}' in the repository's Actions settings.");
            }
            finally
            {
                if (configuration.KeepWorkDirectory)
                    WriteCleanupDiagnostic($"Retained work directory (may contain job data and runner credentials): {runDirectory}");
                else
                {
                    try { OwnedDirectory.DeleteRun(runs, runDirectory); }
                    catch (IOException exception) { WriteCleanupDiagnostic($"Local cleanup failed: {exception.Message}", true); }
                    catch (UnauthorizedAccessException) { WriteCleanupDiagnostic($"Local cleanup denied: {runDirectory}", true); }
                }
            }
        }
    }

    public static void RequireWindowsArchitecture(RunnerConfiguration configuration)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Running this CLI's MXC worker requires Windows 11 24H2 or later.");
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        if (configuration.Architecture != architecture)
            throw new InvalidOperationException($"Configured architecture '{configuration.Architecture}' must match host architecture '{architecture}'.");
    }
}
