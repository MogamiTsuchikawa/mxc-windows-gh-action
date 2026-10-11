using System.Text.Json;
using Mxc.GitHubRunner.Configuration;

namespace Mxc.GitHubRunner.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mxc-configuration-tests", Guid.NewGuid().ToString("N"));

    public ConfigurationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task DefaultConfigurationLoadsButRequiresRepositoryForRun()
    {
        var configuration = await Load("{}");

        Assert.Equal("latest", configuration.RunnerVersion);
        Assert.Equal("x64", configuration.Architecture);
        Assert.Equal(Path.Combine(directory, ".mxc-runner"), configuration.StateDirectory);
        Assert.Equal(120, configuration.TimeoutMinutes);
        Assert.Equal(["mxc"], configuration.Labels);
        Assert.False(configuration.KeepWorkDirectory);
        Assert.Throws<ConfigurationException>(configuration.ValidateForRun);
    }

    [Fact]
    public async Task RelativePathsUseConfigurationDirectoryAndKeepPathOrder()
    {
        Directory.CreateDirectory(Path.Combine(directory, "tools"));
        var configuration = await Load("""
            { "repository": "HTTPS://GitHub.com/Example/repo/", "stateDirectory": "state/../runner-state",
              "toolPaths": ["tools", "./tools"], "architecture": "ARM64", "runnerVersion": "LATEST",
              "labels": ["mxc", "MXC", "custom"] }
            """);

        Assert.Equal("https://github.com/Example/repo", configuration.Repository);
        Assert.Equal(Path.Combine(directory, "runner-state"), configuration.StateDirectory);
        Assert.Equal([Path.Combine(directory, "tools")], configuration.ToolPaths);
        Assert.Equal("arm64", configuration.Architecture);
        Assert.Equal("latest", configuration.RunnerVersion);
        Assert.Equal(["mxc", "custom"], configuration.Labels);
        configuration.ValidateForRun();
    }

    [Theory]
    [InlineData("{\"token\":\"do-not-log-me\"}")]
    [InlineData("{\"repositoryUrl\":\"https://github.com/a/b\"}")]
    [InlineData("{\"timeoutMinutes\":\"120\"}")]
    [InlineData("{\"labels\":null}")]
    [InlineData("{\"labels\":[null]}")]
    [InlineData("{\"toolPaths\":null}")]
    [InlineData("{\"toolPaths\":[null]}")]
    [InlineData("{\"repository\":null}")]
    [InlineData("{\"stateDirectory\":\" \"}")]
    [InlineData("{\"timeoutMinutes\":0}")]
    [InlineData("{\"timeoutMinutes\":1441}")]
    [InlineData("{\"architecture\":\"x86\"}")]
    [InlineData("{\"runnerVersion\":\"v2.333.0\"}")]
    [InlineData("{\"runnerVersion\":\"2.333\"}")]
    [InlineData("{\"namePrefix\":\"-flag\"}")]
    [InlineData("{\"labels\":[\"label,another\"]}")]
    [InlineData("{\"timeoutMinutes\":1,\"TimeoutMinutes\":2}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{broken}")]
    public async Task RejectsMalformedOrUnsafeConfiguration(string json)
    {
        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => Load(json));
        Assert.DoesNotContain("do-not-log-me", exception.Message);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://github.com.evil.example/owner/repo")]
    [InlineData("https://token@github.com/owner/repo")]
    [InlineData("https://github.com:443/owner/repo")]
    [InlineData("https://github.com/owner/repo?token=secret")]
    [InlineData("https://github.com/owner/repo#fragment")]
    [InlineData("https://github.com/owner/repo/actions")]
    [InlineData("https://github.com/owner/%72epo")]
    [InlineData("https://github.com/owner/..")]
    [InlineData("https://github.com/owner/.")]
    [InlineData("https://github.com/owner/repo\n")]
    [InlineData("https://github.com/owner\\repo")]
    public void RepositoryValidationRejectsNoncanonicalDestinations(string repository) =>
        Assert.Throws<ConfigurationException>(() => RepositoryValidator.Normalize(repository));

    [Fact]
    public async Task RejectsFilesystemRootForState()
    {
        var json = JsonSerializer.Serialize(new { stateDirectory = Path.GetPathRoot(directory) });
        await Assert.ThrowsAsync<ConfigurationException>(() => Load(json));
    }

    [Fact]
    public async Task RejectsMissingToolDirectory()
    {
        await Assert.ThrowsAsync<ConfigurationException>(() => Load("{\"toolPaths\":[\"missing\"]}"));
    }

    [Fact]
    public async Task RejectsFilesAsStateOrToolDirectories()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "file.txt"), "content");
        await Assert.ThrowsAsync<ConfigurationException>(() => Load("{\"toolPaths\":[\"file.txt\"]}"));
        await Assert.ThrowsAsync<ConfigurationException>(() => Load("{\"stateDirectory\":\"file.txt\"}"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    public async Task AcceptsTimeoutBoundariesAndPinnedVersion(int minutes)
    {
        var configuration = await Load(JsonSerializer.Serialize(new { timeoutMinutes = minutes, runnerVersion = "2.333.0", labels = Array.Empty<string>() }));
        Assert.Equal(minutes, configuration.TimeoutMinutes);
        Assert.Equal("2.333.0", configuration.RunnerVersion);
        Assert.Empty(configuration.Labels);
    }

    [Fact]
    public async Task RejectsExcessivelyLongIdentifiers()
    {
        await Assert.ThrowsAsync<ConfigurationException>(() => Load(JsonSerializer.Serialize(new { namePrefix = new string('a', 41) })));
        await Assert.ThrowsAsync<ConfigurationException>(() => Load(JsonSerializer.Serialize(new { labels = new[] { new string('a', 65) } })));
    }

    private async Task<RunnerConfiguration> Load(string json)
    {
        var path = Path.Combine(directory, "runner.json");
        await File.WriteAllTextAsync(path, json);
        return await ConfigurationFile.LoadAsync(path);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
