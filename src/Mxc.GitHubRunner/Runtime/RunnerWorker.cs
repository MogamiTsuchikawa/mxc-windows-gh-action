using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("Mxc.GitHubRunner.Tests")]

namespace Mxc.GitHubRunner.Runtime;

public sealed record WorkerRequest(string Repository, string Name, string[] Labels, string RunnerDirectory,
    string WorkDirectory, string RegistrationToken)
{
    public override string ToString() => $"WorkerRequest {{ Name = {Name}, RegistrationToken = *** }}";
}

public static class RunnerWorker
{
    // This entry point is launched only inside MXC. Bootstrap credentials arrive on a pipe.
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // Windows may map AppContainer temp storage below LOCALAPPDATA\Packages.
        // Its path is known only after entering the container, and the isolated
        // profile granted by the host is initially empty.
        Directory.CreateDirectory(Path.GetTempPath());
        using var bootstrap = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var input = await bootstrap.ReadLineAsync(cancellationToken);
        var request = JsonSerializer.Deserialize<WorkerRequest>(input ?? "")
            ?? throw new InvalidDataException("Missing worker bootstrap data.");
        var listener = Path.Combine(request.RunnerDirectory, "bin", "Runner.Listener.exe");
        if (!File.Exists(listener)) throw new FileNotFoundException("Runner.Listener.exe is missing.", listener);
        var configure = CreateStartInfo(listener, request.RunnerDirectory,
            ["configure", "--unattended", "--ephemeral", "--disableupdate", "--url", request.Repository,
                "--name", request.Name, "--work", request.WorkDirectory, "--labels", string.Join(',', request.Labels)]);
        configure.Environment["ACTIONS_RUNNER_INPUT_TOKEN"] = request.RegistrationToken;
        int exit;
        try { exit = await ExecuteAsync(configure, request.RegistrationToken, cancellationToken, stdout, stderr); }
        finally { configure.Environment.Remove("ACTIONS_RUNNER_INPUT_TOKEN"); }
        if (exit != 0) return exit;
        await stdout.WriteLineAsync("Runner configured; waiting for one GitHub Actions job.");
        return await ExecuteAsync(CreateStartInfo(listener, request.RunnerDirectory, ["run"]),
            request.RegistrationToken, cancellationToken, stdout, stderr);
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string directory, string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task<int> ExecuteAsync(ProcessStartInfo info, string secret, CancellationToken cancellationToken,
        TextWriter stdout, TextWriter stderr)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Cannot start GitHub runner.");
        child.StandardInput.Close();
        using var pipeCancellation = new CancellationTokenSource();
        var output = CopyAsync(child.StandardOutput, stdout, secret, pipeCancellation.Token);
        var error = CopyAsync(child.StandardError, stderr, secret, pipeCancellation.Token);
        var wait = child.WaitForExitAsync(CancellationToken.None);
        var pending = new List<Task> { output, error, wait };
        try
        {
            while (!wait.IsCompleted)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(cancellationToken);
                await completed;
                pending.Remove(completed);
            }
            await wait;
            // An abandoned job child can inherit output handles. Let the worker
            // exit so the owning MXC handle can reclaim the complete process tree.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (TimeoutException)
            {
                pipeCancellation.Cancel();
                try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return child.ExitCode;
        }
        catch
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            finally { pipeCancellation.Cancel(); }
            try { await wait; } catch { }
            try { await Task.WhenAll(output, error); } catch { }
            throw;
        }
    }

    private static async Task CopyAsync(StreamReader source, TextWriter destination, string secret, CancellationToken cancellationToken)
    {
        while (await source.ReadLineAsync(cancellationToken) is { } line)
            await destination.WriteLineAsync((string.IsNullOrEmpty(secret) ? line : line.Replace(secret, "***", StringComparison.Ordinal)).AsMemory(), cancellationToken);
    }
}
