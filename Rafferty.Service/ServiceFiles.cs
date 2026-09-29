using Rafferty.Shared;

namespace Rafferty.Service;

internal static class ServiceFiles
{
    private static readonly string DefaultStrategies = Path.Combine(AppContext.BaseDirectory, "strategies.json");
    private static readonly string DefaultListsDirectory = Path.Combine(AppContext.BaseDirectory, "lists");

    public static void EnsureInstalledDefaults()
    {
        CopyIfMissing(DefaultStrategies, AppPaths.StrategiesFile);

        // Strategies reference several bundled hostlists. Provision every file,
        // while preserving any list the user has already customised.
        if (Directory.Exists(DefaultListsDirectory))
        {
            foreach (var source in Directory.EnumerateFiles(DefaultListsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                CopyIfMissing(source, Path.Combine(AppPaths.ListsDirectory, Path.GetFileName(source)));
            }
        }
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(destination) && File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }
}

