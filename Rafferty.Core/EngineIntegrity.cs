using System.Security.Cryptography;
using System.Text.Json;
using Rafferty.Shared;

namespace Rafferty.Core;

public static class EngineIntegrity
{
    public static async Task VerifyAsync(string engineDirectory, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(engineDirectory, "engine-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("Engine integrity manifest is missing.");
        }

        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<EngineManifest>(manifestStream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Engine integrity manifest is invalid.");
        foreach (var (name, expectedHash) in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(engineDirectory, name));
            var root = Path.GetFullPath(engineDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                throw new InvalidDataException($"Required engine file is missing: {name}");
            }
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Engine integrity check failed: {name}");
            }
        }
    }

    private sealed record EngineManifest(string Source, string ReleaseArchiveSha256, Dictionary<string, string> Files);
}

