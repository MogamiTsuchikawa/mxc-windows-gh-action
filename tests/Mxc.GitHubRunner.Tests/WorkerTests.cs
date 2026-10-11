using System.Diagnostics;
using System.Text;
using Mxc.GitHubRunner.Isolation;
using Mxc.GitHubRunner.Runtime;

namespace Mxc.GitHubRunner.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task BrokenOutputTerminatesRunnerInsteadOfWaitingForItsLifetime()
    {
        if (!OperatingSystem.IsWindows()) return;
        var info = Command("echo output & ping -n 30 127.0.0.1 >nul");
        var error = await Assert.ThrowsAsync<IOException>(() => RunnerWorker.ExecuteAsync(
            info, "", CancellationToken.None, new ThrowingWriter(), TextWriter.Null)
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("output closed", error.Message);
    }

    [Fact]
    public async Task CancellationTerminatesRunnerAndCompletesPipeReaders()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        var output = new SignallingWriter();
        var execution = RunnerWorker.ExecuteAsync(Command("echo started & ping -n 30 127.0.0.1 >nul"),
            "", cancellation.Token, output, TextWriter.Null);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WorkerMasksRegistrationTokenBeforeForwardingOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = new StringWriter();
        Assert.Equal(0, await RunnerWorker.ExecuteAsync(Command("echo some-secret-token"),
            "some-secret-token", CancellationToken.None, output, TextWriter.Null));
        Assert.Contains("***", output.ToString());
        Assert.DoesNotContain("some-secret-token", output.ToString());
    }

    [Fact]
    public void RecordFormattingDoesNotPrintBootstrapSecrets()
    {
        const string secret = "unprintable-bootstrap-secret";
        var worker = new WorkerRequest("https://github.com/a/b", "runner", [], "runner", "work", secret);
        var spec = new MxcExecutionSpec("worker", "work", new Dictionary<string, string> { ["TOKEN"] = secret }, [], [], 60)
        {
            StandardInput = secret,
            SensitiveValues = [secret],
        };
        Assert.DoesNotContain(secret, worker.ToString());
        Assert.DoesNotContain(secret, spec.ToString());
    }

    private static ProcessStartInfo Command(string command)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add(command);
        return info;
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("output closed"));
    }

    private sealed class SignallingWriter : TextWriter
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Written.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
