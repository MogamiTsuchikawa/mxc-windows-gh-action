using System.Text.RegularExpressions;

namespace Mxc.GitHubRunner.Configuration;

/// <summary>Non-secret settings for a single ephemeral GitHub Actions runner.</summary>
public sealed record RunnerConfiguration
{
    public string Repository { get; init; } = "https://github.com/OWNER/REPO";
    public string RunnerVersion { get; init; } = "latest";
    public string Architecture { get; init; } = "x64";
    public string StateDirectory { get; init; } = ".mxc-runner";
    public string NamePrefix { get; init; } = "mxc";
    public string[] Labels { get; init; } = ["mxc"];
    public string[] ToolPaths { get; init; } = [];
    public int TimeoutMinutes { get; init; } = 120;
    public bool KeepWorkDirectory { get; init; }

    /// <summary>Rejects the initial repository placeholder before contacting GitHub.</summary>
    public void ValidateForRun() => RepositoryValidator.Normalize(Repository);
}

public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
    public ConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

public static partial class RepositoryValidator
{
    /// <summary>Accepts only a public GitHub HTTPS repository URL and removes a trailing slash.</summary>
    public static string Normalize(string repository, bool allowPlaceholder = false)
    {
        if (repository is null)
            throw new ConfigurationException("repository must be an HTTPS GitHub repository URL.");

        var match = RepositoryPattern().Match(repository);
        if (!match.Success || match.Groups[2].Value is "." or "..")
            throw new ConfigurationException("repository must have the form https://github.com/OWNER/REPO, without credentials, a port, query, or fragment.");

        var owner = match.Groups[1].Value;
        var name = match.Groups[2].Value;
        if (!allowPlaceholder && owner.Equals("OWNER", StringComparison.OrdinalIgnoreCase) &&
            name.Equals("REPO", StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("Replace the OWNER/REPO repository placeholder in the configuration before running.");

        return $"https://github.com/{owner}/{name}";
    }

    [GeneratedRegex(@"\Ahttps://github\.com/([a-z0-9](?:[a-z0-9-]{0,37}[a-z0-9])?)/([a-z0-9_.-]{1,100})/?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
}
