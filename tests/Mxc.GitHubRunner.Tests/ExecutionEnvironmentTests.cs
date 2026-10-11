using System.Runtime.InteropServices;
using Mxc.GitHubRunner.Configuration;
using Mxc.GitHubRunner.Runtime;

namespace Mxc.GitHubRunner.Tests;

public sealed class ExecutionEnvironmentTests
{
    [Fact]
    public void DefaultToolsPreferCompleteBusyBoxInstallationOverMsysGit()
    {
        using var fixture = new DirectoryFixture();
        var programs = Path.Combine(fixture.Path, "ProgramFiles");
        var application = Path.Combine(fixture.Path, "application");
        var fullGit = Path.Combine(programs, "Git", "cmd");
        var powerShell = Path.Combine(programs, "PowerShell", "7");
        var busyBoxRoot = Path.Combine(application, "tools", "MinGitBusyBox");
        var busyBoxCmd = Path.Combine(busyBoxRoot, "cmd");
        Directory.CreateDirectory(fullGit);
        Directory.CreateDirectory(powerShell);
        Directory.CreateDirectory(busyBoxCmd);
        File.WriteAllText(Path.Combine(busyBoxCmd, "git.exe"), "stub");
        // An incomplete extraction must not shadow an existing Git installation.
        Assert.Equal(new[] { fullGit, powerShell }, ExecutionEnvironment.FindDefaultTools(programs, application));
        var runtime = Path.Combine(busyBoxRoot, "ucrt64", "bin");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "busybox.exe"), "stub");
        Assert.Equal(new[] { fullGit, powerShell }, ExecutionEnvironment.FindDefaultTools(programs, application));
        File.WriteAllText(Path.Combine(runtime, "git.exe"), "stub");
        Assert.Equal(new[] { busyBoxCmd, powerShell }, ExecutionEnvironment.FindDefaultTools(programs, application));
    }

    [Fact]
    public void MsysWarningFollowsFirstGitOnPathAndDoesNotFlagResidualDll()
    {
        using var fixture = new DirectoryFixture();
        string CreateGit(string name, bool hasMsysShell)
        {
            var root = Path.Combine(fixture.Path, name);
            var cmd = Path.Combine(root, "cmd");
            Directory.CreateDirectory(cmd);
            Directory.CreateDirectory(Path.Combine(root, "ucrt64"));
            var usr = Path.Combine(root, "usr", "bin");
            Directory.CreateDirectory(usr);
            File.WriteAllText(Path.Combine(cmd, "git.exe"), "stub");
            File.WriteAllText(Path.Combine(usr, "msys-2.0.dll"), "stub");
            if (hasMsysShell) File.WriteAllText(Path.Combine(usr, "sh.exe"), "stub");
            return cmd;
        }
        var fullGit = CreateGit("Git", hasMsysShell: true);
        var busyBox = CreateGit("MinGitBusyBox", hasMsysShell: false);
        Assert.True(ExecutionEnvironment.UsesMsysGit([fullGit, busyBox]));
        Assert.False(ExecutionEnvironment.UsesMsysGit([busyBox, fullGit]));
        Assert.False(ExecutionEnvironment.UsesMsysGit([fixture.Path]));
    }

    [Fact]
    public void StateUnderCliReadGrantIsRejectedWithoutCreatingSiblingCredentialStorage()
    {
        var state = Path.Combine(AppContext.BaseDirectory, "state-" + Guid.NewGuid().ToString("N"));
        var configuration = new RunnerConfiguration { StateDirectory = state };
        var error = Assert.Throws<ConfigurationException>(() => ExecutionEnvironment.ValidateStateDirectory(configuration));
        Assert.Contains("CLI installation", error.Message);
        Assert.False(Directory.Exists(state));
        Assert.Throws<ConfigurationException>(() => ExecutionEnvironment.Create(configuration,
            Path.Combine(state, "runs", Guid.NewGuid().ToString("N")), "worker"));
        Assert.False(Directory.Exists(state));
    }

    [Theory]
    [InlineData("state", "state/tools")]
    [InlineData("tools/state", "tools")]
    [InlineData("same", "same")]
    public void StateAndToolTreesCannotOverlapInEitherDirection(string stateRelative, string toolsRelative)
    {
        using var fixture = new DirectoryFixture();
        var state = Path.Combine(fixture.Path, stateRelative.Replace('/', Path.DirectorySeparatorChar));
        var tool = Path.Combine(fixture.Path, toolsRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(tool);
        Assert.Throws<ConfigurationException>(() => ExecutionEnvironment.ValidateStateDirectory(
            new RunnerConfiguration { StateDirectory = state, ToolPaths = [tool] }));
    }

    [Fact]
    public void SeparateStateTreeWithSimilarPrefixIsAllowed()
    {
        using var fixture = new DirectoryFixture();
        var tool = Path.Combine(fixture.Path, "tools");
        var state = Path.Combine(fixture.Path, "tools-state");
        Directory.CreateDirectory(tool);
        ExecutionEnvironment.ValidateStateDirectory(new RunnerConfiguration { StateDirectory = state, ToolPaths = [tool] });
        Assert.False(Directory.Exists(state));
    }

    [Theory]
    [InlineData("mingw64")]
    [InlineData("ucrt64")]
    [InlineData("clangarm64")]
    public void GitHelperTreesAreReadableWithoutAddingThemToPath(string helpers)
    {
        using var fixture = new DirectoryFixture();
        var git = Path.Combine(fixture.Path, "Git");
        var cmd = Path.Combine(git, "cmd");
        Directory.CreateDirectory(cmd);
        Directory.CreateDirectory(Path.Combine(git, helpers));
        File.WriteAllText(Path.Combine(cmd, "git.exe"), "stub");
        var spec = ExecutionEnvironment.Create(new RunnerConfiguration { StateDirectory = Path.Combine(fixture.Path, "state"), ToolPaths = [cmd] },
            Path.Combine(fixture.Path, "run"), "worker");
        Assert.Contains(git, spec.ReadOnlyPaths);
        Assert.Contains(cmd, spec.Environment["PATH"].Split(';'));
        Assert.DoesNotContain(git, spec.Environment["PATH"].Split(';'));
        Assert.Equal(Path.Combine(fixture.Path, "run", "profile", "AppData", "Local"), spec.Environment["LOCALAPPDATA"]);
        Assert.Equal("{}\n", File.ReadAllText(Path.Combine(fixture.Path, "run", "global.json")));
        foreach (var filename in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
            Assert.Equal("<Project />\n", File.ReadAllText(Path.Combine(fixture.Path, "run", filename)));
    }

    [Fact]
    public void WorkspaceBoundaryFilesNeverOverwriteExistingUserContent()
    {
        using var fixture = new DirectoryFixture();
        var run = Path.Combine(fixture.Path, "run");
        Directory.CreateDirectory(run);
        var global = Path.Combine(run, "global.json");
        File.WriteAllText(global, "original");
        Assert.Throws<IOException>(() => ExecutionEnvironment.Create(new RunnerConfiguration { StateDirectory = run }, run, "worker"));
        Assert.Equal("original", File.ReadAllText(global));
    }

    [Fact]
    public void CleanupDoesNotModifyAttributesThroughAReadOnlyHardLink()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DirectoryFixture();
        var runs = Path.Combine(fixture.Path, "runs");
        var run = Path.Combine(runs, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var outside = Path.Combine(fixture.Path, "sentinel.txt");
        var link = Path.Combine(run, "linked.txt");
        File.WriteAllText(outside, "keep");
        Assert.True(CreateHardLink(link, outside, IntPtr.Zero), $"CreateHardLink error {Marshal.GetLastWin32Error()}");
        File.SetAttributes(outside, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<IOException>(() => OwnedDirectory.DeleteRun(runs, run));
            Assert.True((File.GetAttributes(outside) & FileAttributes.ReadOnly) != 0);
            Assert.Equal("keep", File.ReadAllText(outside));
        }
        finally { File.SetAttributes(outside, FileAttributes.Normal); }
        OwnedDirectory.DeleteRun(runs, run);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void CleanupAcceptsTrailingSeparatorsAndClearsOwnedReadOnlyFiles()
    {
        using var fixture = new DirectoryFixture();
        var runs = Path.Combine(fixture.Path, "runs");
        var run = Path.Combine(runs, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var file = Path.Combine(run, "readonly.txt");
        File.WriteAllText(file, "temporary");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        OwnedDirectory.DeleteRun(runs + Path.DirectorySeparatorChar, run + Path.DirectorySeparatorChar);
        Assert.False(Directory.Exists(run));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existingFilename, IntPtr securityAttributes);

    private sealed class DirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mxc-worker-test-" + Guid.NewGuid().ToString("N"));
        public DirectoryFixture() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
