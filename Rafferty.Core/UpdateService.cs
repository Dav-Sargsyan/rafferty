using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed partial class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    public event Action<string>? Diagnostic;

    public UpdateService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _ownsClient = httpClient is null;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Rafferty", AppVersion.Display));
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string releaseApiUrl = AppPaths.UpdateReleaseApiUrl,
        Version? currentVersion = null,
        CancellationToken token = default)
    {
        RequireHttps(releaseApiUrl, "Update check");
        Log($"Update check URL: {releaseApiUrl}");
        Log("Manifest URL: not used (GitHub Releases API)");
        using var response = await GetWithRetryAsync(releaseApiUrl, "release", token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var manifest = document.RootElement.TryGetProperty("tag_name", out _)
            ? ParseGitHubRelease(document.RootElement)
            : document.RootElement.Deserialize<UpdateManifest>(JsonDefaults.Options)
                ?? throw new InvalidDataException("The update response is empty.");
        ValidateManifest(manifest);
        var latest = ParseVersion(manifest.Version);
        var current = currentVersion ?? AppVersion.Current;
        return new(latest > current, current, latest, manifest);
    }

    private UpdateManifest ParseGitHubRelease(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("GitHub release has no tag_name.");
        var releaseUrl = release.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        Log($"Release URL: {releaseUrl ?? "not provided"}");
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var names = assets.Select(asset => asset.GetProperty("name").GetString() ?? "(unnamed)").ToArray();
        Log($"Release assets: {(names.Length == 0 ? "(none)" : string.Join(", ", names))}");
        var selected = assets.FirstOrDefault(asset => string.Equals(asset.GetProperty("name").GetString(), AppPaths.UpdateAssetName, StringComparison.Ordinal));
        if (selected.ValueKind == JsonValueKind.Undefined)
            throw new UpdateAssetNotFoundException($"В релизе {tag} не найден файл {AppPaths.UpdateAssetName}.");

        var assetUrl = selected.GetProperty("browser_download_url").GetString()
            ?? throw new InvalidDataException($"Asset {AppPaths.UpdateAssetName} has no browser_download_url.");
        Log($"Asset URL: {assetUrl}");
        var digest = selected.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset {AppPaths.UpdateAssetName} has no SHA-256 digest.");
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
        return new(tag, assetUrl, digest[7..], notes);
    }

    public async Task<string> DownloadAsync(
        UpdateManifest manifest,
        string destination,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        ValidateManifest(manifest);
        Log($"Asset URL: {manifest.DownloadUrl}");
        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var partial = fullDestination + ".part";
        try
        {
            using var response = await GetWithRetryAsync(manifest.DownloadUrl, "asset", token).ConfigureAwait(false);
            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    received += count;
                    if (total is > 0) progress?.Report(received * 100d / total.Value);
                }
            }

            string actual;
            await using (var verification = File.OpenRead(partial))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(verification, token).ConfigureAwait(false));
            }
            if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update SHA-256 mismatch. Expected {manifest.Sha256}; received {actual}.");
            File.Move(partial, fullDestination, true);
            progress?.Report(100);
            return fullDestination;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, string stage, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return response;
                var status = response.StatusCode;
                Log($"HTTP {(int)status}; Requested URL: {url}; Stage: {stage}");
                if ((int)status >= 500 && attempt == 0)
                {
                    response.Dispose();
                    continue;
                }
                response.Dispose();
                throw new UpdateRequestException(status, url, stage);
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested && attempt == 0)
            {
                Log($"Timeout; Requested URL: {url}; Stage: {stage}; retry=1");
            }
        }
    }

    private void Log(string message) => Diagnostic?.Invoke(message);

    public static Version ParseVersion(string value)
    {
        value = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(value, out var version)
            ? version
            : throw new InvalidDataException($"Invalid update version: {value}");
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        _ = ParseVersion(manifest.Version);
        RequireHttps(manifest.DownloadUrl, "Update download");
        if (!Sha256Pattern().IsMatch(manifest.Sha256)) throw new InvalidDataException("Update manifest contains an invalid SHA-256 value.");
    }

    private static void RequireHttps(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"{name} URL must use HTTPS.");
    }

    [GeneratedRegex("^[A-Fa-f0-9]{64}$")]
    private static partial Regex Sha256Pattern();

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

public sealed class UpdateAssetNotFoundException(string message) : Exception(message);

public sealed class UpdateRequestException(HttpStatusCode statusCode, string url, string stage)
    : HttpRequestException($"HTTP {(int)statusCode}; Requested URL: {url}; Stage: {stage}", null, statusCode)
{
    public string RequestedUrl { get; } = url;
    public string Stage { get; } = stage;
}
