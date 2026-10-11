using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Mxc.Sdk.V1;

namespace Mxc.GitHubRunner.Isolation;

/// <summary>Owns a native MXC process and drains all its pipes for its complete lifetime.</summary>
public sealed class MxcExecutor : IMxcExecutor
{
    private readonly Func<ContainerRequest, Task<IMxcProcess>> _spawn;

    public MxcExecutor() : this(async request => await MxcContainer.SpawnAsync(request)) { }

    // The SDK exposes IMxcProcess specifically for deterministic lifecycle testing.
    public MxcExecutor(Func<ContainerRequest, Task<IMxcProcess>> spawn)
    {
        _spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));
    }

    public async Task<MxcExecutionResult> ExecuteAsync(
        MxcExecutionSpec spec,
        Action<string> stdout,
        Action<string> stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var request = BuildRequest(spec);
        cancellationToken.ThrowIfCancellationRequested();
        var outputGate = new object();
        var stdoutSink = stdout;
        var stderrSink = stderr;
        stdout = text => { lock (outputGate) stdoutSink(text); };
        stderr = text => { lock (outputGate) stderrSink(text); };

        // Do not abandon an in-flight native spawn: its eventual handle must be
        // killed/disposed before the caller can delete the runner workspace.
        using var process = await _spawn(request).ConfigureAwait(false);
        var stdoutStream = process.StandardOutput;
        var stderrStream = process.StandardError;
        using var stdoutCloser = process.StandardOutputCloser;
        using var stderrCloser = process.StandardErrorCloser;
        var output = PumpAsync(stdoutStream, stdout, spec.SensitiveValues);
        var error = PumpAsync(stderrStream, stderr, spec.SensitiveValues);
        var input = WriteInputAsync(process.StandardInput, spec.StandardInput);

        // Start waiting immediately: MXC's streaming timeout is enforced by WaitAsync.
        // Keep this wait uncancelled; cancellation below explicitly kills the tree.
        var wait = process.WaitAsync();
        var operations = new List<Task> { output, error, input, wait };
        var reportedWarnings = new HashSet<string>(StringComparer.Ordinal);
        void ReportWarnings()
        {
            foreach (var warning in process.Warnings)
                if (reportedWarnings.Add(warning))
                    stderr("MXC: " + Redact(warning, spec.SensitiveValues) + System.Environment.NewLine);
        }
        try
        {
            ReportWarnings();

            while (operations.Count > 0)
            {
                var completed = await Task.WhenAny(operations).WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                operations.Remove(completed);
            }

            var result = await wait.ConfigureAwait(false);
            ReportWarnings();
            cancellationToken.ThrowIfCancellationRequested();
            return new MxcExecutionResult(result.ExitCode, result.TimedOut);
        }
        catch (Exception failure)
        {
            // Stream failures also terminate the workload, so a failed output sink
            // cannot leave it blocked on an undrained pipe. Preserve the original error.
            try { process.Kill(); }
            catch
            {
                // Native handle disposal has its own process-tree cleanup fallback.
                try { process.Dispose(); } catch { }
            }
            try { await wait.ConfigureAwait(false); } catch { }
            try { stdoutCloser?.Close(); } catch { }
            try { stderrCloser?.Close(); } catch { }
            try { await Task.WhenAll(output, error, input).ConfigureAwait(false); } catch { }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    public static ContainerRequest BuildRequest(MxcExecutionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);
        if (spec.TimeoutSeconds <= 0 || spec.TimeoutSeconds > uint.MaxValue / 1000)
            throw new ArgumentOutOfRangeException(nameof(spec), "TimeoutSeconds must be between 1 and 4294967.");

        return new ContainerRequest(spec.Command)
        {
            Containment = new Containment.ProcessContainer
            {
                // Runner startup enumerates every ancestor to validate its
                // installation path. Listing grants do not grant file reads or
                // writes in sibling directories. Unsupported hosts fail closed.
                Filesystem = new ProcessContainerFilesystemPolicy
                {
                    EnumeratePaths = EnumerateAncestors(spec.WorkingDirectory,
                        spec.ReadOnlyPaths.Concat(spec.ReadWritePaths)),
                },
            },
            WorkingDirectory = spec.WorkingDirectory,
            Environment = new Dictionary<string, string>(spec.Environment, StringComparer.OrdinalIgnoreCase),
            InheritDefaultEnvironment = false,
            TimeoutMs = checked((uint)spec.TimeoutSeconds * 1000),
            Filesystem = new FilesystemPolicy
            {
                ReadonlyPaths = spec.ReadOnlyPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ReadwritePaths = spec.ReadWritePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ClearPolicyOnExit = true,
            },
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
                Ingress = new NetworkIngressPolicy
                {
                    Default = NetworkAction.Deny,
                    HostLoopback = NetworkAction.Deny,
                },
            },
            // CoreCLR requires Windows UI subsystem APIs during initialization.
            // Clipboard and input injection remain independently denied.
            Ui = new UiPolicy { Disable = false, Clipboard = ClipboardPolicy.None, AllowInputInjection = false },
        };
    }

    private static List<string> EnumerateAncestors(string directory, IEnumerable<string> readablePaths)
    {
        var ancestors = new List<string>();
        var readable = readablePaths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
        for (var parent = Directory.GetParent(Path.GetFullPath(directory)); parent is not null; parent = parent.Parent)
        {
            // Native enumeration rules must not narrow an existing recursive
            // read/write rule when the command runs in a nested working directory.
            var alreadyReadable = readable.Any(path =>
                parent.FullName.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                parent.FullName.StartsWith(Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            if (!alreadyReadable) ancestors.Add(parent.FullName);
        }
        return ancestors;
    }

    public static MxcHostInfo ProbeHost()
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "Windows is required for the ProcessContainer backend.", null, []);

        try
        {
            var support = MxcPlatform.GetPlatformSupport();
            if (!support.IsSupported || !support.AvailableMethods.Contains(ContainmentBackend.ProcessContainer))
                return new(false, support.Reason ?? "MXC ProcessContainer is unavailable on this host.", null, []);
            var probe = MxcContainer.Probe();
            return new(probe.Error is null && probe.Tier is not null, probe.Error,
                probe.Tier?.ToString(), probe.Warnings);
        }
        catch (Exception exception) when (exception is MxcException or DllNotFoundException
            or BadImageFormatException or EntryPointNotFoundException)
        {
            return new(false, exception.Message, null, []);
        }
    }

    private static Task WriteInputAsync(Stream? stream, string? text) => Task.Run(async () =>
    {
        if (stream is null)
        {
            if (text is not null)
                throw new IOException("MXC did not provide the bootstrap input pipe.");
            return;
        }
        using (stream)
        {
            if (text is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                try
                {
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                finally
                {
                    Array.Clear(bytes);
                }
            }
        }
    });

    private static Task PumpAsync(Stream? stream, Action<string> sink, IReadOnlyList<string> secrets) =>
        Task.Run(async () =>
        {
            if (stream is null) return;
            using var reader = new StreamReader(stream, new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var redactor = new StreamingRedactor(secrets);
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                var text = redactor.Append(new string(buffer, 0, count), final: false);
                if (text.Length > 0) sink(text);
            }
            var tail = redactor.Append(string.Empty, final: true);
            if (tail.Length > 0) sink(tail);
        });

    private static string Redact(string value, IReadOnlyList<string> secrets) =>
        new StreamingRedactor(secrets).Append(value, final: true);

    private sealed class StreamingRedactor
    {
        private readonly string[] _secrets;
        private readonly int _retained;
        private string _pending = string.Empty;

        public StreamingRedactor(IReadOnlyList<string> secrets)
        {
            _secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct()
                .OrderByDescending(s => s.Length).ToArray();
            _retained = _secrets.Length == 0 ? 0 : _secrets[0].Length - 1;
        }

        public string Append(string text, bool final)
        {
            _pending += text;
            var limit = final ? _pending.Length : Math.Max(0, _pending.Length - _retained);
            var output = new StringBuilder();
            var offset = 0;
            while (offset < limit)
            {
                var secret = _secrets.FirstOrDefault(s =>
                    _pending.AsSpan(offset).StartsWith(s, StringComparison.Ordinal));
                if (secret is not null)
                {
                    output.Append("***");
                    offset += secret.Length;
                }
                else
                {
                    output.Append(_pending[offset++]);
                }
            }
            _pending = _pending[offset..];
            return output.ToString();
        }
    }
}
