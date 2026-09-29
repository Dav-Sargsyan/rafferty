using System.Reflection;
using System.Security.Cryptography;
using System.IO;
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
            var temporary = destination + ".new";
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }

        if (!File.Exists(AppPaths.EngineExecutable) || !File.Exists(AppPaths.StrategiesFile))
        {
            throw new InvalidDataException("Embedded network runtime could not be extracted.");
        }

        EnsureUserList("ipset-exclude-user.txt", "203.0.113.113/32" + Environment.NewLine);
        EnsureUserList("list-general-user.txt", "# Never leave this file empty" + Environment.NewLine + "domain.example.abc" + Environment.NewLine);
        EnsureUserList("list-exclude-user.txt", "domain.example.abc" + Environment.NewLine);
    }

    private static void EnsureUserList(string name, string defaultContent)
    {
        var path = Path.Combine(AppPaths.ListsDirectory, name);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) File.WriteAllText(path, defaultContent);
    }
}
