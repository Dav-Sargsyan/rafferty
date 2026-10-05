namespace Rafferty.Core;

/// <summary>
/// Immutable requirements for the pinned Flowseal-compatible ALT3 launch.
/// The command itself remains in the golden strategy snapshot; this list
/// prevents a nominally equal command from starting without its resources.
/// </summary>
public static class ClassicReferenceProfile
{
    public const string Alt3StrategyId = "general--alt3";

    public static IReadOnlyList<string> RequiredEngineFiles { get; } =
    [
        "winws.exe", "WinDivert.dll", "WinDivert64.sys",
        "quic_initial_www_google_com.bin", "ACTIVE_DISCORD_UDP.bin", "ACTIVE_GAME_UDP.bin",
        "tls_clienthello_max_ru.bin"
    ];

    public static IReadOnlyList<string> RequiredListFiles { get; } =
    [
        "list-general.txt", "list-general-user.txt", "list-google.txt",
        "list-exclude.txt", "list-exclude-user.txt", "ipset-all.txt",
        "ipset-exclude.txt", "ipset-exclude-user.txt"
    ];
}
