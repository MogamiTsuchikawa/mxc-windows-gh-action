using System.Text;
using Microsoft.Mxc.Sdk.V1;
using Mxc.GitHubRunner.Isolation;

namespace Mxc.GitHubRunner.Tests;

public sealed class MxcExecutorTests
{
    private static MxcExecutionSpec Spec() => new("worker.exe", @"C:\runner",
        new Dictionary<string, string> { ["TEMP"] = @"C:\runner\temp" },
        [@"C:\tools"], [@"C:\runner"], 60);

    [Fact]
    public void RunnerAncestorListingDoesNotGrantAncestorFileAccess()
    {
        var working = Path.Combine(Path.GetTempPath(), "mxc-policy-test", "runs", "session");
        var spec = Spec() with { WorkingDirectory = working, ReadWritePaths = [working] };
        var request = MxcExecutor.BuildRequest(spec);
        var containment = Assert.IsType<Containment.ProcessContainer>(request.Containment);
        var parent = Directory.GetParent(working)!.FullName;
        Assert.Contains(parent, containment.Filesystem!.EnumeratePaths);
        Assert.DoesNotContain(parent, request.Filesystem!.ReadonlyPaths);
        Assert.DoesNotContain(parent, request.Filesystem.ReadwritePaths);
        Assert.False(request.Ui!.Disable);
        Assert.Equal(ClipboardPolicy.None, request.Ui.Clipboard);
        Assert.False(request.Ui.AllowInputInjection);
    }

    [Fact]
    public void AncestorListingDoesNotNarrowAnExistingWritableTree()
    {
        var writable = Path.Combine(Path.GetTempPath(), "mxc-policy-test", "session");
        var working = Path.Combine(writable, "work", "project");
        var request = MxcExecutor.BuildRequest(Spec() with
        {
            WorkingDirectory = working,
            ReadWritePaths = [writable],
        });
        var containment = Assert.IsType<Containment.ProcessContainer>(request.Containment);
        Assert.DoesNotContain(writable, containment.Filesystem!.EnumeratePaths);
        Assert.DoesNotContain(Path.Combine(writable, "work"), containment.Filesystem.EnumeratePaths);
        Assert.Contains(Directory.GetParent(writable)!.FullName, containment.Filesystem.EnumeratePaths);
    }

    [Fact]
    public async Task CancellationKillsAndReapsBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess();
        var executor = new MxcExecutor(_ => Task.FromResult<IMxcProcess>(process));

        var execution = executor.ExecuteAsync(Spec(), _ => { }, _ => { }, cancellation.Token);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(process.Killed);
        Assert.True(process.Reaped);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task OutputFailureKillsChildRatherThanLeavingUndrainedPipes()
    {
        var process = new FakeProcess(stdout: "workload output");
        var executor = new MxcExecutor(_ => Task.FromResult<IMxcProcess>(process));
        var failure = await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(
            Spec(), _ => throw new IOException("output unavailable"), _ => { }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("output unavailable", failure.Message);
        Assert.True(process.Killed);
        Assert.True(process.Reaped);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task BootstrapUsesStdinAndSecretsAreRedactedAcrossOutputBoundaries()
    {
        const string token = "registration-token-for-test";
        var prefix = new string('x', 4090);
        var process = new FakeProcess(stdout: prefix + token + " done", stderr: "error " + token);
        process.WarningValues = ["warning " + token];
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        ContainerRequest? captured = null;
        var executor = new MxcExecutor(request =>
        {
            captured = request;
            return Task.FromResult<IMxcProcess>(process);
        });
        process.Complete(new WaitResult { ExitCode = 5, TimedOut = true });

        var result = await executor.ExecuteAsync(Spec() with
        {
            StandardInput = "{\"token\":\"" + token + "\"}",
            SensitiveValues = [token],
        }, text => stdout.Append(text), text => stderr.Append(text), CancellationToken.None);

        Assert.Equal(new MxcExecutionResult(5, true), result);
        Assert.Equal(prefix + "*** done", stdout.ToString());
        Assert.DoesNotContain(token, stderr.ToString());
        Assert.Contains("warning ***", stderr.ToString());
        Assert.Equal("{\"token\":\"" + token + "\"}", Encoding.UTF8.GetString(process.Input.ToArray()));
        Assert.True(process.Input.Closed);
        Assert.DoesNotContain(token, captured!.Command);
        Assert.DoesNotContain(token, captured.Environment!.Values);
        Assert.True(process.Reaped);
    }

    [Fact]
    public async Task CancellationDuringSpawnStillDisposesTheEventualNativeHandle()
    {
        var spawn = new TaskCompletionSource<IMxcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess();
        using var cancellation = new CancellationTokenSource();
        var executor = new MxcExecutor(_ => spawn.Task);
        var execution = executor.ExecuteAsync(Spec(), _ => { }, _ => { }, cancellation.Token);
        cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        spawn.SetResult(process);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task MissingBootstrapPipeFailsAndTerminatesTheChild()
    {
        var process = new FakeProcess { NoInput = true };
        var executor = new MxcExecutor(_ => Task.FromResult<IMxcProcess>(process));

        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(
            Spec() with { StandardInput = "bootstrap" }, _ => { }, _ => { }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    private sealed class RecordingStream : MemoryStream
    {
        public bool Closed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FakeProcess(string stdout = "", string stderr = "") : IMxcProcess
    {
        private readonly TaskCompletionSource<WaitResult> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RecordingStream Input { get; } = new();
        public bool NoInput { get; init; }
        public bool Killed { get; private set; }
        public bool Reaped { get; private set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<string> WarningValues { get; set; } = [];
        public uint Id => 1;
        public Stream? StandardInput => NoInput ? null : Input;
        public Stream StandardOutput { get; } = new MemoryStream(Encoding.UTF8.GetBytes(stdout));
        public Stream StandardError { get; } = new MemoryStream(Encoding.UTF8.GetBytes(stderr));
        public IMxcStreamCloser? StandardOutputCloser => null;
        public IMxcStreamCloser? StandardErrorCloser => null;
        public IReadOnlyList<string> Warnings => WarningValues;
        public ExecutionMetadata? OutputMetadata => null;
        public WaitResult Wait() => WaitAsync().GetAwaiter().GetResult();
        public async Task<WaitResult> WaitAsync(CancellationToken cancellationToken = default)
        {
            WaitStarted.TrySetResult();
            var result = await _exit.Task.WaitAsync(cancellationToken);
            Reaped = true;
            return result;
        }
        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result.ExitCode : 0;
            return _exit.Task.IsCompletedSuccessfully;
        }
        public Task<(WaitResult Result, byte[] Stdout, byte[] Stderr)> WaitForExitWithOutputAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Complete(WaitResult result) => _exit.TrySetResult(result);
        public void Kill()
        {
            Killed = true;
            Complete(new WaitResult { ExitCode = 1 });
        }
        public void Dispose()
        {
            Disposed = true;
            Input.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}
