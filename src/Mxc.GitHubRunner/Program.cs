using System.Runtime.InteropServices;
using System.Text.Json;
using Mxc.GitHubRunner.Configuration;
using Mxc.GitHubRunner.GitHub;
using Mxc.GitHubRunner.Isolation;
using Mxc.GitHubRunner.Runtime;

namespace Mxc.GitHubRunner;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try { return await ExecuteAsync(args, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var message = exception.Message;
            foreach (var variable in new[] { "MXC_GITHUB_TOKEN", "MXC_RUNNER_REGISTRATION_TOKEN" })
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } secret)
                    message = message.Replace(secret, "***", StringComparison.Ordinal);
            Console.Error.WriteLine($"Error: {message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    private static async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"] or ["help"])
        {
            PrintUsage();
            return 0;
        }
        if (args is ["__worker"]) return await RunnerWorker.RunAsync(cancellationToken);
        if (args is ["__smoke"]) return await SmokeWorkerAsync(cancellationToken);
        if (args[0] == "doctor")
        {
            if (args.Length != 1) throw new ArgumentException("doctor does not accept options.");
            var host = MxcExecutor.ProbeHost();
            Console.WriteLine($"Operating system: {RuntimeInformation.OSDescription}");
            Console.WriteLine($"Architecture: {RuntimeInformation.OSArchitecture}");
            Console.WriteLine($"MXC ProcessContainer: {(host.IsSupported ? "available" : "unavailable")}");
            if (host.IsolationTier is { } tier) Console.WriteLine($"Isolation tier: {tier}");
            if (host.Reason is { } reason) Console.Error.WriteLine(reason);
            foreach (var warning in host.Warnings) Console.Error.WriteLine($"MXC: {warning}");
            return host.IsSupported ? 0 : 1;
        }
        if (args[0] is not ("init" or "plan" or "prepare" or "run" or "smoke"))
            throw new ArgumentException("Unknown command. Run mxc-gh-runner --help for usage.");
        var path = args switch
        {
            [_] => "mxc-runner.json",
            [_, "--config", var value] when !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal) => value,
            _ => throw new ArgumentException("Expected COMMAND [--config PATH]. No other options are supported."),
        };
        if (args[0] == "init")
        {
            var configuration = new RunnerConfiguration
            {
                Architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64",
                ToolPaths = ExecutionEnvironment.FindDefaultTools(),
            };
            var fullPath = Path.GetFullPath(path);
            var configurationDirectory = Path.GetDirectoryName(fullPath)!;
            try
            {
                ExecutionEnvironment.ValidateStateDirectory(configuration with
                {
                    StateDirectory = Path.GetFullPath(configuration.StateDirectory, configurationDirectory),
                });
            }
            catch (ConfigurationException)
            {
                // An extracted application directory is a readonly grant in MXC.
                // Keep new run data outside it even when init is launched there.
                var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localData))
                    throw new ConfigurationException("Cannot choose a state directory outside the application installation. Use --config with a path outside readonly tool directories.");
                configuration = configuration with { StateDirectory = Path.Combine(localData, "MxcGitHubRunner") };
                ExecutionEnvironment.ValidateStateDirectory(configuration);
            }
            Directory.CreateDirectory(configurationDirectory);
            await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken);
            Console.WriteLine($"Created {fullPath}");
            Console.WriteLine("Set repository to your GitHub repository URL before running a job.");
            if (ExecutionEnvironment.UsesMsysGit(configuration.ToolPaths))
                Console.Error.WriteLine("Warning: the selected Git uses MSYS shell helpers, which cannot initialize inside current MXC ProcessContainer. For actions/checkout, configure official MinGit BusyBox; see docs/git-compatibility.md.");
            return 0;
        }
        var loaded = await ConfigurationFile.LoadAsync(path, cancellationToken);
        ExecutionEnvironment.ValidateStateDirectory(loaded);
        if (args[0] == "plan")
        {
            Console.WriteLine(JsonSerializer.Serialize(loaded, JsonOptions));
            return 0;
        }
        if (args[0] == "smoke") return await SmokeAsync(loaded, cancellationToken);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var service = new RunnerService(new GitHubRunnerClient(http), new MxcExecutor());
        if (args[0] == "prepare")
        {
            await service.PrepareAsync(loaded, cancellationToken);
            return 0;
        }
        return await service.RunAsync(loaded, cancellationToken);
    }

    private static async Task<int> SmokeAsync(RunnerConfiguration configuration, CancellationToken cancellationToken)
    {
        RunnerService.RequireWindowsArchitecture(configuration);
        var runs = Path.Combine(configuration.StateDirectory, "runs");
        var runDirectory = Path.Combine(runs, Guid.NewGuid().ToString("N"));
        OwnedDirectory.AssertNoReparsePoints(runs);
        try
        {
            Directory.CreateDirectory(runDirectory);
            var spec = ExecutionEnvironment.Create(configuration, runDirectory,
                WindowsCommandLine.CurrentApplication("__smoke"), "mxc-smoke\n") with { TimeoutSeconds = 30 };
            var result = await new MxcExecutor().ExecuteAsync(spec, Console.Write, Console.Error.Write, cancellationToken);
            if (result.TimedOut)
            {
                Console.Error.WriteLine("MXC smoke check timed out.");
                return 124;
            }
            if (result.ExitCode == 0) Console.WriteLine("MXC smoke check passed.");
            return result.ExitCode;
        }
        finally { OwnedDirectory.DeleteRun(runs, runDirectory); }
    }

    private static async Task<int> SmokeWorkerAsync(CancellationToken cancellationToken)
    {
        if (await Console.In.ReadToEndAsync(cancellationToken) != "mxc-smoke\n")
            throw new InvalidDataException("The MXC bootstrap input did not arrive intact.");
        if (Environment.GetEnvironmentVariable("MXC_GITHUB_TOKEN") is not null ||
            Environment.GetEnvironmentVariable("MXC_RUNNER_REGISTRATION_TOKEN") is not null)
            throw new InvalidOperationException("The MXC worker inherited a host credential variable.");
        // AppContainer may redirect the runtime temp path beneath LOCALAPPDATA.
        var temp = Path.GetTempPath();
        Directory.CreateDirectory(temp);
        var path = Path.Combine(temp, $"mxc-smoke-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "mxc-smoke", cancellationToken);
            if (await File.ReadAllTextAsync(path, cancellationToken) != "mxc-smoke")
                throw new IOException("The MXC temporary directory round trip failed.");
        }
        finally { File.Delete(path); }
        Console.WriteLine($"MXC worker: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine("Bootstrap input, isolated credential environment, and temporary file access verified.");
        return 0;
    }

    private static void PrintUsage() => Console.WriteLine("""
        mxc-gh-runner — run one ephemeral GitHub Actions runner inside Windows MXC

        Usage:
          mxc-gh-runner init    [--config PATH]   Create a configuration; never overwrite
          mxc-gh-runner doctor                   Check the Windows MXC backend
          mxc-gh-runner plan    [--config PATH]   Validate and print resolved configuration
          mxc-gh-runner smoke   [--config PATH]   Test MXC locally without GitHub credentials
          mxc-gh-runner prepare [--config PATH]   Download and verify the official runner
          mxc-gh-runner run     [--config PATH]   Register, run one job, and clean up
          mxc-gh-runner --help

        The default configuration path is ./mxc-runner.json.
        For run, set MXC_GITHUB_TOKEN or MXC_RUNNER_REGISTRATION_TOKEN in the host environment.
        Keep credentials out of configuration files and command-line arguments.
        Exit codes: 0 = command succeeded, 1 = error, 124 = timeout, 130 = cancelled.
        The run command otherwise returns the runner process exit code, not the workflow result.
        """);
}
