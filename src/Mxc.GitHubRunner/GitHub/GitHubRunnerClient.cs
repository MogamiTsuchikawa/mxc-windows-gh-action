using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mxc.GitHubRunner.GitHub;

public sealed record RunnerRelease(string Version, string Architecture, Uri DownloadUrl, string Sha256, string FileName);
public sealed record RunnerRegistrationToken(string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"RunnerRegistrationToken {{ Token = [REDACTED], ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>Downloads official runners and manages repository-scoped, short-lived registration.</summary>
public sealed class GitHubRunnerClient
{
    private const long MaximumDownloadBytes = 1024L * 1024 * 1024;
    private const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumJsonBytes = 4 * 1024 * 1024;
    private readonly HttpClient httpClient;

    public GitHubRunnerClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        EnsureNoDefaultAuthorization();
    }

    public async Task<RunnerRelease> ResolveReleaseAsync(string version, string architecture, CancellationToken cancellationToken = default)
    {
        if (architecture is not ("x64" or "arm64"))
            throw new ArgumentException("Runner architecture must be x64 or arm64.", nameof(architecture));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("A runner version or 'latest' is required.", nameof(version));
        var normalizedVersion = version.StartsWith('v') ? version[1..] : version;
        if (normalizedVersion != "latest" && !IsVersion(normalizedVersion))
            throw new ArgumentException("Runner version must have the form 2.338.0, or 'latest'.", nameof(version));

        var path = normalizedVersion == "latest" ? "latest" : "tags/v" + normalizedVersion;
        using var document = await SendJsonAsync(HttpMethod.Get, "repos/actions/runner/releases/" + path, null, cancellationToken);
        var root = document.RootElement;
        var tag = RequiredString(root, "tag_name");
        if (!tag.StartsWith('v') || !IsVersion(tag[1..]))
            throw new InvalidDataException("GitHub returned an invalid runner release version.");
        var resolvedVersion = tag[1..];
        if (normalizedVersion != "latest" && normalizedVersion != resolvedVersion)
            throw new InvalidDataException("GitHub returned a different runner release version.");
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("Draft runner releases cannot be installed.");

        var fileName = $"actions-runner-win-{architecture}-{resolvedVersion}.zip";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Runner release does not contain downloadable assets.");
        var matches = assets.EnumerateArray().Where(asset =>
            asset.TryGetProperty("name", out var name) && name.GetString() == fileName).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("Runner release does not contain exactly one matching Windows package.");

        var asset = matches[0];
        if (!Uri.TryCreate(RequiredString(asset, "browser_download_url"), UriKind.Absolute, out var downloadUrl))
            throw new InvalidDataException("Runner release has an invalid download URL.");
        ValidateDownloadUrl(downloadUrl, resolvedVersion, fileName);
        string? hash = null;
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
            digest.GetString() is { } digestValue && digestValue.StartsWith("sha256:", StringComparison.Ordinal))
            hash = digestValue[7..];
        if (hash is null && root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
        {
            var match = Regex.Match(body.GetString() ?? "", $"<!-- BEGIN SHA win-{architecture} -->\\s*([a-fA-F0-9]{{64}})\\s*<!-- END SHA win-{architecture} -->", RegexOptions.CultureInvariant);
            if (match.Success) hash = match.Groups[1].Value;
        }
        if (hash is null || !IsSha256(hash))
            throw new InvalidDataException("Runner release does not provide a valid SHA-256 checksum.");
        return new RunnerRelease(resolvedVersion, architecture, downloadUrl, hash.ToLowerInvariant(), fileName);
    }

    /// <summary>Publishes an extracted package only after checksum and archive validation succeed.</summary>
    public async Task DownloadAndExtractAsync(RunnerRelease release, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!IsVersion(release.Version) || release.Architecture is not ("x64" or "arm64") || !IsSha256(release.Sha256) ||
            release.FileName != $"actions-runner-win-{release.Architecture}-{release.Version}.zip")
            throw new ArgumentException("Runner release metadata is invalid.", nameof(release));
        ValidateDownloadUrl(release.DownloadUrl, release.Version, release.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("Runner destination must not already exist.");
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Runner destination must have a parent directory.", nameof(destination));
        Directory.CreateDirectory(parent);
        var unique = ".mxc-runner-" + Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(parent, unique + ".zip");
        var stagingPath = Path.Combine(parent, unique);
        try
        {
            using var request = CreateRequest(HttpMethod.Get, release.DownloadUrl, null);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
                throw new InvalidDataException("Runner download exceeds the supported package size.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total = checked(total + count);
                    if (total > MaximumDownloadBytes) throw new InvalidDataException("Runner download exceeds the supported package size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("Runner package SHA-256 checksum verification failed.");
            }

            Directory.CreateDirectory(stagingPath);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                long extracted = 0;
                if (archive.Entries.Count > 100000) throw new InvalidDataException("Runner archive contains too many files.");
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryPath = GetSafeEntryPath(stagingPath, entry);
                    extracted = checked(extracted + entry.Length);
                    if (extracted > MaximumExtractedBytes) throw new InvalidDataException("Runner archive exceeds the supported extracted size.");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        if (entry.Length != 0) throw new InvalidDataException("Runner archive contains an invalid directory entry.");
                        Directory.CreateDirectory(entryPath);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
                    await using var source = entry.Open();
                    await using var output = new FileStream(entryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    await CopyEntryAsync(source, output, entry.Length, cancellationToken);
                }
            }
            if (!File.Exists(Path.Combine(stagingPath, "bin", "Runner.Listener.exe")))
                throw new InvalidDataException("Runner archive does not contain bin/Runner.Listener.exe.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingPath, target);
        }
        finally
        {
            // Both paths are generated immediate children of the verified destination parent.
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
        }
    }

    public async Task<RunnerRegistrationToken> CreateRegistrationTokenAsync(Uri repositoryUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        var repository = GetRepositoryPath(repositoryUrl);
        ValidateAccessToken(accessToken);
        using var document = await SendJsonAsync(HttpMethod.Post, repository + "/actions/runners/registration-token", accessToken, cancellationToken);
        var token = RequiredString(document.RootElement, "token");
        if (!DateTimeOffset.TryParse(RequiredString(document.RootElement, "expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires))
            throw new InvalidDataException("GitHub returned an invalid token expiry.");
        return new RunnerRegistrationToken(token, expires);
    }

    /// <summary>Removes only the unique exact-name match; a missing runner is already clean.</summary>
    public async Task<bool> RemoveRunnerAsync(Uri repositoryUrl, string runnerName, string accessToken, CancellationToken cancellationToken = default)
    {
        var repository = GetRepositoryPath(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerName);
        ValidateAccessToken(accessToken);
        var runnerIds = new HashSet<long>();
        for (var page = 1; page <= 10000; page++)
        {
            using var document = await SendJsonAsync(HttpMethod.Get, $"{repository}/actions/runners?per_page=100&page={page}", accessToken, cancellationToken);
            if (!document.RootElement.TryGetProperty("runners", out var runners) || runners.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub returned an invalid runner list.");
            foreach (var runner in runners.EnumerateArray())
            {
                if (RequiredString(runner, "name") == runnerName)
                {
                    if (!runner.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value) || value <= 0)
                        throw new InvalidDataException("GitHub returned an invalid runner identifier.");
                    runnerIds.Add(value);
                }
            }
            if (runners.GetArrayLength() < 100) break;
            if (page == 10000) throw new InvalidDataException("GitHub runner list exceeded the supported pagination limit.");
        }
        if (runnerIds.Count == 0) return false;
        if (runnerIds.Count > 1) throw new InvalidDataException("More than one runner has the requested name; cleanup cannot safely choose one.");
        using var request = CreateRequest(HttpMethod.Delete, new Uri("https://api.github.com/" + repository + "/actions/runners/" + runnerIds.Single()), accessToken);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        EnsureSuccess(response);
        return true;
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, string? accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, new Uri("https://api.github.com/" + path), accessToken);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength > MaximumJsonBytes)
            throw new InvalidDataException("GitHub response exceeds the supported size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (memory.Length + count > MaximumJsonBytes) throw new InvalidDataException("GitHub response exceeds the supported size.");
            memory.Write(buffer, 0, count);
        }
        try { return JsonDocument.Parse(memory.ToArray()); }
        catch (JsonException) { throw new InvalidDataException("GitHub returned invalid JSON."); }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string? accessToken)
    {
        EnsureNoDefaultAuthorization();
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("mxc-github-runner/1.0");
        if (uri.Host == "api.github.com")
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        return request;
    }

    private void EnsureNoDefaultAuthorization()
    {
        if (httpClient.DefaultRequestHeaders.Contains("Authorization"))
            throw new ArgumentException("Use an HttpClient without default Authorization headers; credentials are scoped to API requests.", nameof(httpClient));
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub request failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("GitHub response is missing required metadata.");
        return value.GetString()!;
    }

    private static bool IsVersion(string value) => Regex.IsMatch(value, @"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant);
    private static bool IsSha256(string value) => value is not null && Regex.IsMatch(value, @"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);

    private static void ValidateDownloadUrl(Uri url, string version, string fileName)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.AbsolutePath != $"/actions/runner/releases/download/v{version}/{fileName}")
            throw new InvalidDataException("Runner package URL must point to the official GitHub runner release.");
    }

    private static string GetRepositoryPath(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("Repository URL must be https://github.com/OWNER/REPO.", nameof(url));
        var match = Regex.Match(url.AbsolutePath, @"\A/([A-Za-z0-9][A-Za-z0-9-]*)/([A-Za-z0-9_.-]+)/?\z", RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups[2].Value is "." or ".." || match.Groups[2].Value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Repository URL must be https://github.com/OWNER/REPO without a .git suffix.", nameof(url));
        return $"repos/{match.Groups[1].Value}/{match.Groups[2].Value}";
    }

    private static void ValidateAccessToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Any(char.IsWhiteSpace))
            throw new ArgumentException("A GitHub API access token is required.", nameof(accessToken));
    }

    private static string GetSafeEntryPath(string root, ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        var parts = name.TrimEnd('/').Split('/');
        if (name.StartsWith('/') || parts.Any(part => part.Length == 0 || part is "." or ".." ||
            part.IndexOfAny([':', '\0', '<', '>', '"', '|', '?', '*']) >= 0 || part.EndsWith('.') || part.EndsWith(' ')) ||
            parts.Any(part => Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Runner archive contains an unsafe entry path.");
        var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000 || (entry.ExternalAttributes & 0x400) != 0)
            throw new InvalidDataException("Runner archive contains a link or unsupported entry type.");
        var fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runner archive entry escapes the extraction directory.");
        return fullPath;
    }

    private static async Task CopyEntryAsync(Stream source, Stream destination, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            copied = checked(copied + count);
            if (copied > expectedLength) throw new InvalidDataException("Runner archive entry exceeds its declared size.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        if (copied != expectedLength) throw new InvalidDataException("Runner archive entry is incomplete.");
    }
}
