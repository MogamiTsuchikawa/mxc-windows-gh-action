using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mxc.GitHubRunner.Configuration;

public static partial class ConfigurationFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Loads strict JSON and resolves local paths relative to the configuration file.</summary>
    public static async Task<RunnerConfiguration> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = File.OpenRead(fullPath);
        RunnerConfiguration configuration;
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ConfigurationException("The configuration must be a JSON object.");

            var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!properties.Add(property.Name))
                    throw new ConfigurationException("Duplicate configuration properties are not allowed, including names that differ only by case.");

            configuration = document.RootElement.Deserialize<RunnerConfiguration>(JsonOptions)
                ?? throw new ConfigurationException("The configuration must be a JSON object.");
        }
        catch (JsonException exception)
        {
            // Do not echo JSON input: an accidentally pasted credential must not enter logs.
            throw new ConfigurationException("Invalid configuration JSON. Check property names and value types; unknown properties (including tokens) are not allowed.", exception);
        }

        return Normalize(configuration, Path.GetDirectoryName(fullPath)!);
    }

    private static RunnerConfiguration Normalize(RunnerConfiguration configuration, string baseDirectory)
    {
        var repository = RepositoryValidator.Normalize(configuration.Repository, allowPlaceholder: true);
        if (string.IsNullOrWhiteSpace(configuration.RunnerVersion) ||
            !(configuration.RunnerVersion.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
              (configuration.RunnerVersion.Length <= 32 && VersionPattern().IsMatch(configuration.RunnerVersion))))
            throw new ConfigurationException("runnerVersion must be 'latest' or a three-part version such as '2.333.0'.");

        if (configuration.Architecture is null ||
            !(configuration.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
              configuration.Architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase)))
            throw new ConfigurationException("architecture must be 'x64' or 'arm64'.");

        if (configuration.NamePrefix is null || configuration.NamePrefix.Length is < 1 or > 40 ||
            !IdentifierPattern().IsMatch(configuration.NamePrefix))
            throw new ConfigurationException("namePrefix must be 1-40 ASCII letters, digits, periods, underscores, or hyphens, and start with a letter or digit.");

        if (configuration.Labels is null || configuration.Labels.Length > 20 ||
            configuration.Labels.Any(label => label is null || label.Length is < 1 or > 64 || !IdentifierPattern().IsMatch(label)))
            throw new ConfigurationException("labels must contain at most 20 labels, each 1-64 ASCII letters, digits, periods, underscores, or hyphens, starting with a letter or digit.");

        if (configuration.TimeoutMinutes is < 1 or > 1440)
            throw new ConfigurationException("timeoutMinutes must be between 1 and 1440.");

        var stateDirectory = ResolvePath(configuration.StateDirectory, baseDirectory, "stateDirectory");
        if (Path.TrimEndingDirectorySeparator(stateDirectory).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(stateDirectory)!), StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("stateDirectory cannot be a filesystem root.");
        if (File.Exists(stateDirectory))
            throw new ConfigurationException("stateDirectory must be a directory, not an existing file.");

        if (configuration.ToolPaths is null)
            throw new ConfigurationException("toolPaths must be an array of existing directory paths.");
        var toolPaths = configuration.ToolPaths.Select(path => ResolvePath(path, baseDirectory, "toolPaths")).ToArray();
        foreach (var toolPath in toolPaths)
            if (!Directory.Exists(toolPath))
                throw new ConfigurationException("Each toolPaths entry must refer to an existing directory.");

        return configuration with
        {
            Repository = repository,
            RunnerVersion = configuration.RunnerVersion.Equals("latest", StringComparison.OrdinalIgnoreCase) ? "latest" : configuration.RunnerVersion,
            Architecture = configuration.Architecture.ToLowerInvariant(),
            StateDirectory = stateDirectory,
            Labels = configuration.Labels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ToolPaths = toolPaths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray()
        };
    }

    private static string ResolvePath(string path, string baseDirectory, string property)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ConfigurationException($"{property} must contain a nonempty path.");
        try
        {
            return Path.GetFullPath(path, baseDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ConfigurationException($"{property} contains an invalid path.", exception);
        }
    }

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
