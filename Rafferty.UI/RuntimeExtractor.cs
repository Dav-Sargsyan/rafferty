using System.Reflection;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using Rafferty.Shared;

namespace Rafferty.UI;

internal static class RuntimeExtractor
{
    private const string Prefix = "Rafferty.Runtime/";

    public static async Task EnsureAsync(CancellationToken token = default)
    {
        Directory.CreateDirectory(AppPaths.RuntimeRoot);
        try
        {
            File.SetAttributes(Path.GetDirectoryName(AppPaths.RuntimeRoot)!, FileAttributes.Hidden | FileAttributes.Directory);
        }
        catch (UnauthorizedAccessException)
        {
            // Extraction can still proceed if the hidden attribute cannot be set.
        }

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var relative = resourceName[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(AppPaths.RuntimeRoot, relative));
            var root = Path.GetFullPath(AppPaths.RuntimeRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Unsafe embedded runtime path.");
            }

            if (IsUserManagedList(relative) && File.Exists(destination) && new FileInfo(destination).Length > 0)
            {
                continue;
            }

            await using var resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Embedded resource is missing: {resourceName}");
            using var memory = new MemoryStream();
            await resource.CopyToAsync(memory, token).ConfigureAwait(false);
            var bytes = memory.ToArray();
            var expected = SHA256.HashData(bytes);

            if (File.Exists(destination))
            {
                await using var existing = File.OpenRead(destination);
                var actual = await SHA256.HashDataAsync(existing, token).ConfigureAwait(false);
                if (actual.AsSpan().SequenceEqual(expected))
                {
                    continue;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (IsStrategyDatabase(relative) && File.Exists(destination))
                File.Copy(destination, destination + ".previous", true);
            var temporary = destination + ".new";
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }

        if (!File.Exists(AppPaths.ClassicEngineExecutable) || !File.Exists(AppPaths.NextGenEngineExecutable) || !File.Exists(AppPaths.StrategiesFile))
        {
            throw new InvalidDataException("Embedded network runtime could not be extracted.");
        }

        EnsureUserList("ipset-exclude-user.txt", "203.0.113.113/32" + Environment.NewLine);
        EnsureUserList("list-general-user.txt", "# Never leave this file empty" + Environment.NewLine + "domain.example.abc" + Environment.NewLine);
        EnsureUserList("list-exclude-user.txt", "domain.example.abc" + Environment.NewLine);
        await WriteRuntimeManifestAsync(token).ConfigureAwait(false);
    }

    private static void EnsureUserList(string name, string defaultContent)
    {
        var path = Path.Combine(AppPaths.ListsDirectory, name);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) File.WriteAllText(path, defaultContent);
    }

    private static bool IsUserManagedList(string relative)
    {
        var normalized = relative.Replace('\\', '/').ToLowerInvariant();
        return normalized is "lists/ipset-exclude-user.txt" or "lists/list-general-user.txt" or "lists/list-exclude-user.txt";
    }

    private static bool IsStrategyDatabase(string relative) => relative.Replace('\\', '/').ToLowerInvariant() is
        "strategies.json" or "nextgen-strategies.json";

    private static async Task WriteRuntimeManifestAsync(CancellationToken token)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(AppPaths.RuntimeRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(path, AppPaths.RuntimeManifestFile, StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(path);
            files[Path.GetRelativePath(AppPaths.RuntimeRoot, path).Replace('\\', '/')] =
                Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        }
        var manifest = new
        {
            classicRuntimeVersion = AppPaths.ClassicEngineVersion,
            nextGenRuntimeVersion = AppPaths.NextGenEngineVersion,
            strategyPackVersion = AppPaths.StrategyPackVersion,
            files
        };
        var json = JsonSerializer.Serialize(manifest, JsonDefaults.Options);
        if (File.Exists(AppPaths.RuntimeManifestFile)
            && string.Equals(await File.ReadAllTextAsync(AppPaths.RuntimeManifestFile, token).ConfigureAwait(false), json, StringComparison.Ordinal))
            return;
        await File.WriteAllTextAsync(AppPaths.RuntimeManifestFile, json, token).ConfigureAwait(false);
    }
}
