using System.Runtime.InteropServices;
using Mxc.GitHubRunner.Configuration;
using Mxc.GitHubRunner.Isolation;

namespace Mxc.GitHubRunner.Runtime;

public static class ExecutionEnvironment
{
    private static readonly string[] GitRuntimeDirectories = ["mingw64", "mingw32", "ucrt64", "clangarm64"];

    public static MxcExecutionSpec Create(RunnerConfiguration configuration, string runDirectory, string command,
        string? standardInput = null)
    {
        var readOnly = GetReadOnlyRoots(configuration);
        ValidateSeparateDirectory(configuration.StateDirectory, readOnly, "stateDirectory");
        ValidateSeparateDirectory(runDirectory, readOnly, "Run directory");
        OwnedDirectory.AssertNoReparsePoints(runDirectory);
        Directory.CreateDirectory(runDirectory);
        // SDK and MSBuild search upward for these files. Stop that search at
        // the owned workspace so host repository settings cannot be inherited.
        WriteBoundaryFile(Path.Combine(runDirectory, "global.json"), "{}\n");
        foreach (var filename in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
            WriteBoundaryFile(Path.Combine(runDirectory, filename), "<Project />\n");
        var profile = Path.Combine(runDirectory, "profile");
        var temp = Path.Combine(runDirectory, "temp");
        var local = Path.Combine(profile, "AppData", "Local");
        var roaming = Path.Combine(profile, "AppData", "Roaming");
        foreach (var directory in new[] { profile, temp, local, roaming }) Directory.CreateDirectory(directory);
        var dotnetRoot = ResolveDotnetRoot();
        var tools = GetLaunchTools(configuration);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "SystemDrive", "ComSpec", "OS", "NUMBER_OF_PROCESSORS",
                     "PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        environment["PATH"] = string.Join(';', tools);
        environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        environment["TEMP"] = environment["TMP"] = temp;
        environment["USERPROFILE"] = environment["HOME"] = profile;
        environment["HOMEDRIVE"] = Path.GetPathRoot(profile)?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
        environment["HOMEPATH"] = profile[environment["HOMEDRIVE"].Length..];
        environment["LOCALAPPDATA"] = local;
        environment["APPDATA"] = roaming;
        environment["DOTNET_ROOT"] = dotnetRoot;
        environment["DOTNET_CLI_HOME"] = profile;
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        environment["NUGET_PACKAGES"] = Path.Combine(profile, ".nuget", "packages");
        return new MxcExecutionSpec(command, runDirectory, environment, readOnly, [runDirectory],
            checked(configuration.TimeoutMinutes * 60)) { StandardInput = standardInput };
    }

    /// <summary>Checks the state boundary without creating files or contacting GitHub.</summary>
    public static void ValidateStateDirectory(RunnerConfiguration configuration) =>
        ValidateSeparateDirectory(configuration.StateDirectory, GetReadOnlyRoots(configuration), "stateDirectory");

    public static IReadOnlyList<string> GetReadOnlyRoots(RunnerConfiguration configuration)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return GetLaunchTools(configuration).Select(ResolveToolReadRoot)
            .Concat(new[] { AppContext.BaseDirectory, windows }.Where(path => !string.IsNullOrWhiteSpace(path)))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    private static string[] GetLaunchTools(RunnerConfiguration configuration)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemTools = string.IsNullOrEmpty(windows) ? [] : new[]
        {
            Path.Combine(windows, "System32"),
            Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0"),
        };
        return configuration.ToolPaths.Concat(systemTools).Append(ResolveDotnetRoot())
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    private static void ValidateSeparateDirectory(string directory, IReadOnlyList<string> readOnlyRoots, string description)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Contains(string parent, string child) => child.Equals(parent, comparison) ||
            child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
        foreach (var root in readOnlyRoots)
            if (Contains(root, path) || Contains(path, root))
                throw new ConfigurationException($"{description} must be separate from the CLI installation, Windows, .NET, and tool directories. Conflicting read-only directory: {root}");
    }

    private static void WriteBoundaryFile(string path, string content)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }

    internal static string ResolveToolReadRoot(string toolPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(toolPath));
        // Git for Windows' cmd/git.exe dispatches into mingw64 and usr. Grant
        // its installation tree without adding those helper directories to PATH.
        for (var candidate = directory; candidate is not null; candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "cmd", "git.exe")) &&
                GitRuntimeDirectories
                    .Any(name => Directory.Exists(Path.Combine(candidate.FullName, name))))
                return candidate.FullName;
            if (candidate.FullName.Equals(directory.Parent?.Parent?.FullName, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return directory.FullName;
    }

    private static string ResolveDotnetRoot()
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        if (runtime.Parent?.Name == "Microsoft.NETCore.App" && runtime.Parent.Parent?.Name == "shared")
            return runtime.Parent.Parent.Parent!.FullName;
        // A self-contained application has no shared-runtime ancestor. Walking
        // up three directories there would accidentally grant a much wider tree.
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        return File.Exists(Path.Combine(installed, "dotnet.exe")) ? installed : AppContext.BaseDirectory;
    }

    public static string[] FindDefaultTools()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return FindDefaultTools(programFiles, AppContext.BaseDirectory);
    }

    internal static string[] FindDefaultTools(string programFiles, string applicationDirectory)
    {
        var busyBoxRoot = Path.Combine(applicationDirectory, "tools", "MinGitBusyBox");
        var busyBoxGit = Path.Combine(busyBoxRoot, "cmd");
        var git = File.Exists(Path.Combine(busyBoxGit, "git.exe")) &&
            GitRuntimeDirectories.Any(name =>
                File.Exists(Path.Combine(busyBoxRoot, name, "bin", "git.exe")) &&
                File.Exists(Path.Combine(busyBoxRoot, name, "bin", "busybox.exe")))
                ? busyBoxGit : Path.Combine(programFiles, "Git", "cmd");
        return new[] { git, Path.Combine(programFiles, "PowerShell", "7") }
            .Where(Directory.Exists).ToArray();
    }

    public static bool UsesMsysGit(IReadOnlyList<string> toolPaths)
    {
        var selected = toolPaths.FirstOrDefault(path => File.Exists(Path.Combine(path, "git.exe")));
        if (selected is null) return false;
        var root = ResolveToolReadRoot(selected);
        // MinGit-BusyBox can retain an MSYS DLL for ancillary utilities. Its
        // Git shell is native ash, so only diagnose the MSYS shell pair.
        return File.Exists(Path.Combine(root, "usr", "bin", "sh.exe")) &&
            File.Exists(Path.Combine(root, "usr", "bin", "msys-2.0.dll"));
    }
}
