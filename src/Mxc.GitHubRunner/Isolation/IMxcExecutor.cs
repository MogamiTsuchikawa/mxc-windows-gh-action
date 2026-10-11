namespace Mxc.GitHubRunner.Isolation;

public sealed record MxcExecutionSpec(
    string Command,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> ReadOnlyPaths,
    IReadOnlyList<string> ReadWritePaths,
    int TimeoutSeconds)
{
    // Delivered only through the pipe; never put bootstrap credentials on a command line.
    public string? StandardInput { get; init; }
    public IReadOnlyList<string> SensitiveValues { get; init; } = [];

    public override string ToString() =>
        $"MxcExecutionSpec {{ WorkingDirectory = {WorkingDirectory}, TimeoutSeconds = {TimeoutSeconds}, Environment = ***, StandardInput = *** }}";
}

public sealed record MxcExecutionResult(int ExitCode, bool TimedOut);

public interface IMxcExecutor
{
    /// <summary>Callbacks receive text chunks, including their original newlines.</summary>
    Task<MxcExecutionResult> ExecuteAsync(
        MxcExecutionSpec spec,
        Action<string> stdout,
        Action<string> stderr,
        CancellationToken cancellationToken);
}

public sealed record MxcHostInfo(
    bool IsSupported,
    string? Reason,
    string? IsolationTier,
    IReadOnlyList<string> Warnings);
