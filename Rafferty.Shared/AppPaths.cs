namespace Rafferty.Shared;

public static class AppPaths
{
    public const string PipeName = "Rafferty";
    public const string ServiceName = "Rafferty.Service";
    public const string RuntimePackageVersion = "1.4.1-dual-engine";
    public const string RuntimeLayoutVersion = RuntimePackageVersion;
    public const string ClassicEngineVersion = "zapret1-ref-249a704";
    public const string NextGenEngineVersion = "zapret2-v1.0.5.2";
    public const string StrategyPackVersion = "dual-2026.10.01";
    public const string UpdateReleaseApiUrl = "https://api.github.com/repos/Dav-Sargsyan/rafferty/releases/latest";
    public const string UpdateAssetName = "Rafferty.exe";

    public static string UserDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rafferty");

    public static string MachineDataRoot => UserDataRoot;
    public static string RuntimeRoot => Path.Combine(UserDataRoot, "runtime", RuntimeLayoutVersion);

    public static string ConfigFile => Path.Combine(UserDataRoot, "config.json");
    public static string ProfilesFile => Path.Combine(UserDataRoot, "profiles.json");
    public static string StrategiesFile => Path.Combine(RuntimeRoot, "strategies.json");
    public static string NextGenStrategiesFile => Path.Combine(RuntimeRoot, "nextgen-strategies.json");
    public static string GoldenClassicStrategiesFile => Path.Combine(RuntimeRoot, "golden-classic.json");
    public static string CustomStrategiesFile => Path.Combine(UserDataRoot, "custom-strategies.json");
    public static string RuntimeFile => Path.Combine(UserDataRoot, "state.json");
    public static string UpdateStateFile => Path.Combine(UserDataRoot, "update-state.json");
    public static string UpdatesDirectory => Path.Combine(UserDataRoot, "updates");
    public static string ClassicEngineDirectory => Path.Combine(RuntimeRoot, "classic");
    public static string NextGenEngineDirectory => Path.Combine(RuntimeRoot, "nextgen");
    public static string ClassicEngineExecutable => Path.Combine(ClassicEngineDirectory, "winws.exe");
    public static string NextGenEngineExecutable => Path.Combine(NextGenEngineDirectory, "winws2.exe");
    // Compatibility aliases for Classic-only callers.
    public static string EngineDirectory => ClassicEngineDirectory;
    public static string EngineExecutable => ClassicEngineExecutable;
    public static string ListsDirectory => Path.Combine(RuntimeRoot, "lists");
    public static string ServiceListsDirectory => Path.Combine(ListsDirectory, "services");
    public static string ActiveHostlistFile => Path.Combine(ListsDirectory, "active-hostlist.txt");
    public static string ActiveGeneralHostlistFile => Path.Combine(ListsDirectory, "active-general-hostlist.txt");
    public static string RuntimeManifestFile => Path.Combine(RuntimeRoot, "runtime-manifest.json");
    public static string LogsDirectory => Path.Combine(UserDataRoot, "logs");
    public static string LicensesDirectory => Path.Combine(RuntimeRoot, "licenses");
}

