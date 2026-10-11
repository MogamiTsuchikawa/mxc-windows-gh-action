using System.Runtime.InteropServices;
using Mxc.GitHubRunner.Configuration;
using Mxc.GitHubRunner.Runtime;

namespace Mxc.GitHubRunner.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mxc-runtime-tests", Guid.NewGuid().ToString("N"));

    public RuntimeTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task InitInApplicationDirectoryChoosesStateOutsideReadOnlyInstallTree()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"init-check-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(0, await Program.Main(["init", "--config", path]));
            var configuration = await ConfigurationFile.LoadAsync(path);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MxcGitHubRunner"), configuration.StateDirectory);
            ExecutionEnvironment.ValidateStateDirectory(configuration);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void QuotedArgumentsRoundTripThroughWindowsParser()
    {
        if (!OperatingSystem.IsWindows()) return;
        string[] expected = [@"C:\Program Files\MXC\runner.exe", "", "one two", "quote\"inside", @"C:\ends in slash\", @"slash\""quote", "&|<>%!"];
        var command = string.Join(' ', expected.Select(WindowsCommandLine.Quote));
        var arguments = CommandLineToArgvW(command, out var count);
        Assert.NotEqual(IntPtr.Zero, arguments);
        try
        {
            var actual = Enumerable.Range(0, count)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size))).ToArray();
            Assert.Equal(expected, actual);
        }
        finally { LocalFree(arguments); }
    }

    [Theory]
    [InlineData("line\nnext")]
    [InlineData("line\rnext")]
    [InlineData("zero\0byte")]
    public void CommandLineRejectsControlCharacters(string value) =>
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Quote(value));

    [Fact]
    public void CleanupDeletesOnlyTheOwnedRunAndLeavesSiblingData()
    {
        var runs = Path.Combine(directory, "runs");
        var owned = Path.Combine(runs, Guid.NewGuid().ToString("N"));
        var sibling = Path.Combine(runs, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(owned, "nested"));
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(owned, "nested", "job.txt"), "job");
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        File.SetAttributes(Path.Combine(owned, "nested", "job.txt"), FileAttributes.ReadOnly);
        OwnedDirectory.DeleteRun(runs, owned);
        Assert.False(Directory.Exists(owned));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sibling, "keep.txt")));
        Assert.True(Directory.Exists(runs));
    }

    [Fact]
    public void CleanupRejectsOutsideDirectoriesAndNonRunNames()
    {
        var runs = Path.Combine(directory, "runs");
        var outside = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var named = Path.Combine(runs, "keep");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(named);
        Assert.Throws<IOException>(() => OwnedDirectory.DeleteRun(runs, outside));
        Assert.Throws<IOException>(() => OwnedDirectory.DeleteRun(runs, named));
        Assert.Throws<IOException>(() => OwnedDirectory.DeleteRun(runs, runs));
        Assert.True(Directory.Exists(outside));
        Assert.True(Directory.Exists(named));
    }

    [Fact]
    public void WorkerEnvironmentHasExplicitToolsAndFreshWritableProfile()
    {
        var tools = Path.Combine(directory, "tools");
        var run = Path.Combine(directory, "run");
        Directory.CreateDirectory(tools);
        var spec = ExecutionEnvironment.Create(new RunnerConfiguration { StateDirectory = run, ToolPaths = [tools], TimeoutMinutes = 2 }, run, "worker", "bootstrap");
        Assert.StartsWith(tools + ";", spec.Environment["PATH"]);
        Assert.Equal(Path.Combine(run, "profile"), spec.Environment["USERPROFILE"]);
        Assert.Equal(spec.Environment["USERPROFILE"], spec.Environment["HOME"]);
        Assert.Equal(Path.Combine(run, "temp"), spec.Environment["TEMP"]);
        Assert.True(Directory.Exists(spec.Environment["TEMP"]));
        Assert.True(Directory.Exists(spec.Environment["LOCALAPPDATA"]));
        Assert.Equal([run], spec.ReadWritePaths);
        Assert.Contains(tools, spec.ReadOnlyPaths);
        Assert.Equal(120, spec.TimeoutSeconds);
        Assert.Equal("bootstrap", spec.StandardInput);
        Assert.DoesNotContain("MXC_GITHUB_TOKEN", spec.Environment.Keys);
        Assert.DoesNotContain("MXC_RUNNER_REGISTRATION_TOKEN", spec.Environment.Keys);
        Assert.DoesNotContain("GH_TOKEN", spec.Environment.Keys);
        Assert.DoesNotContain("GITHUB_TOKEN", spec.Environment.Keys);
        Assert.DoesNotContain("SSH_AUTH_SOCK", spec.Environment.Keys);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
