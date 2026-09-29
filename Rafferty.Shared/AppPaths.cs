namespace Rafferty.Shared;

public static class AppPaths
{
    public const string PipeName = "Rafferty";
    public const string ServiceName = "Rafferty.Service";
    public const string RuntimeVersion = "1.2.0-ref-249a704";

    public static string UserDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rafferty");

    public static string MachineDataRoot => UserDataRoot;
    public static string RuntimeRoot => Path.Combine(UserDataRoot, "runtime", RuntimeVersion);

    public static string ConfigFile => Path.Combine(UserDataRoot, "config.json");
    public static string ProfilesFile => Path.Combine(UserDataRoot, "profiles.json");
    public static string StrategiesFile => Path.Combine(RuntimeRoot, "strategies.json");
    public static string CustomStrategiesFile => Path.Combine(UserDataRoot, "custom-strategies.json");
    public static string RuntimeFile => Path.Combine(UserDataRoot, "state.json");
    public static string EngineDirectory => Path.Combine(RuntimeRoot, "engine");
    public static string EngineExecutable => Path.Combine(EngineDirectory, "winws.exe");
    public static string ListsDirectory => Path.Combine(RuntimeRoot, "lists");
    public static string LogsDirectory => Path.Combine(UserDataRoot, "logs");
    public static string LicensesDirectory => Path.Combine(RuntimeRoot, "licenses");
}

