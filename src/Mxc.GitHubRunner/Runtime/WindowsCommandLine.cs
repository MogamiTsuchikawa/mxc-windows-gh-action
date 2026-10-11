using System.Text;

namespace Mxc.GitHubRunner.Runtime;

public static class WindowsCommandLine
{
    // Quote for CreateProcess / CommandLineToArgvW, never for cmd.exe.
    public static string Quote(string argument)
    {
        if (argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n'))
            throw new ArgumentException("A command argument contains control characters.");
        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            output.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            output.Append(character);
            slashes = 0;
        }
        output.Append('\\', slashes * 2);
        return output.Append('"').ToString();
    }

    public static string CurrentApplication(string argument)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the application executable.");
        var prefix = Quote(executable);
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            prefix += " " + Quote(typeof(WindowsCommandLine).Assembly.Location);
        return prefix + " " + Quote(argument);
    }
}
