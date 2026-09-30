using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed partial class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsClient = httpClient is null;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Rafferty", AppVersion.Display));
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestUrl = AppPaths.UpdateManifestUrl,
        Version? currentVersion = null,
        CancellationToken token = default)
    {
        RequireHttps(manifestUrl, "Update manifest");
        using var response = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonDefaults.Options, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The update manifest is empty.");
        ValidateManifest(manifest);
        var latest = ParseVersion(manifest.Version);
        var current = currentVersion ?? AppVersion.Current;
        return new(latest > current, current, latest, manifest);
    }

    public async Task<string> DownloadAsync(
        UpdateManifest manifest,
        string destination,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        ValidateManifest(manifest);
        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var partial = fullDestination + ".part";
        try
        {
            using var response = await _http.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
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
