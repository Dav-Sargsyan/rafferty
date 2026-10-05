using System.Text.Json.Serialization;

namespace Rafferty.Shared;

[JsonConverter(typeof(JsonStringEnumConverter<EngineType>))]
public enum EngineType
{
    Auto,
    Classic,
    NextGen
}

[JsonConverter(typeof(JsonStringEnumConverter<PipeCommand>))]
public enum PipeCommand
{
    Start,
    Stop,
    Restart,
    Status,
    ApplyStrategy,
    TestStrategy,
    RunDiagnostics,
    Optimize,
    ReloadLists
}

public sealed record PipeRequest(
    PipeCommand Command,
    string? StrategyId = null,
    string? RequestId = null);

public sealed record PipeResponse(
    bool Success,
    string Message,
    EngineSnapshot? Status = null,
    IReadOnlyList<DiagnosticResult>? Diagnostics = null,
    OptimizationResult? Optimization = null,
    string? RequestId = null);

public sealed record EngineSnapshot(
    bool ServiceOnline,
    bool EngineRunning,
    int? ProcessId,
    string? StrategyId,
    DateTimeOffset? StartedAt,
    string? LastError,
    ReachabilitySnapshot Reachability,
    int CrashCount = 0,
    bool IsAdministrator = false,
    bool DriverActive = false,
    bool StrategyApplied = false,
    bool ConnectivityVerified = false,
    string? CommandLine = null,
    EngineType EngineType = EngineType.Classic);

public sealed record ReachabilitySnapshot(
    ServiceReachability Internet,
    ServiceReachability YouTube,
    ServiceReachability Discord,
    ServiceReachability DiscordVoice,
    DateTimeOffset? CheckedAt = null,
    IReadOnlyDictionary<string, ServiceReachability>? ServiceResults = null)
{
    public static ReachabilitySnapshot Unknown { get; } = new(
        ServiceReachability.NotTested,
        ServiceReachability.NotTested,
        ServiceReachability.NotTested,
        ServiceReachability.NotTested);
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceReachability>))]
public enum ServiceReachability
{
    NotTested,
    Working,
    Degraded,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticState>))]
public enum DiagnosticState
{
    Success,
    Warning,
    Error,
    NotTested
}

// A response from a remote host only proves that transport reached that host.
// Keep that fact separate from a successful, service-specific validation.
[JsonConverter(typeof(JsonStringEnumConverter<ConnectivityState>))]
public enum ConnectivityState
{
    NotTested,
    TransportOnly,
    Partial,
    Working,
    Blocked,
    ServerRejected,
    Failed,
    Inconclusive
}

[JsonConverter(typeof(JsonStringEnumConverter<BlockClassification>))]
public enum BlockClassification
{
    Unknown,
    DnsProblem,
    TcpBlocked,
    TlsBlocked,
    QuicBlocked,
    LikelyDpi,
    ServerRejected,
    MediaBlocked,
    ProtocolBlocked
}

[JsonConverter(typeof(JsonStringEnumConverter<IpSetMode>))]
public enum IpSetMode
{
    None,
    Loaded,
    Any
}

public sealed record DiagnosticResult(
    string Id,
    string Name,
    DiagnosticState State,
    string Detail,
    double? LatencyMs = null,
    ConnectivityState Connectivity = ConnectivityState.NotTested,
    BlockClassification Classification = BlockClassification.Unknown,
    int? HttpStatus = null,
    string? ContentType = null,
    string? FinalUri = null,
    bool TransportReachable = false,
    bool ServiceValidated = false);

public sealed record StrategyScore(
    string StrategyId,
    double Score,
    double SuccessRate,
    double AverageLatencyMs,
    double PacketLoss,
    int Errors,
    TimeSpan StartupTime,
    ServiceReachability YouTube = ServiceReachability.NotTested,
    ServiceReachability Discord = ServiceReachability.NotTested,
    ServiceReachability Voice = ServiceReachability.NotTested,
    bool EngineStarted = true,
    bool DriverActive = true,
    string? FailureReason = null,
    IReadOnlyDictionary<string, ServiceReachability>? TargetResults = null);

/// <summary>
/// A validated service profile, scoped to a non-identifying network fingerprint.
/// Multiple entries can later be composed into one engine command.
/// </summary>
public sealed record ServiceStrategyResult(
    string ServiceId,
    EngineType Engine,
    string StrategyFamily,
    IReadOnlyList<string> Arguments,
    string Protocol,
    DateTimeOffset LastValidated,
    string NetworkFingerprint,
    double SuccessScore);

public sealed record OptimizationResult(
    bool Success,
    string? SelectedStrategyId,
    IReadOnlyList<StrategyScore> Scores,
    IReadOnlyList<DiagnosticResult> Baseline,
    string Message,
    bool FullyWorking = false);

public sealed record UserSettings(
    bool StartWithWindows = false,
    bool StartMinimized = false,
    bool AutoOptimizeOnNewNetwork = true,
    bool AutoRecovery = true,
    bool Notifications = true,
    bool AdvancedMode = false,
    string Theme = "Dark",
    bool StartEnabled = false,
    string Language = "ru-RU",
    bool ManualMode = false,
    string ManualStrategyId = "general",
    bool MinimizeToTray = true,
    bool CheckYouTube = true,
    bool CheckDiscord = true,
    bool CheckVoice = true,
    IpSetMode IpSetMode = IpSetMode.Loaded,
    bool GameFilterEnabled = false,
    string GameFilterTcp = "1024-65535",
    string GameFilterUdp = "1024-65535",
    bool AutoFindOnFailure = true,
    bool RecheckOnStartup = false,
    bool AutoCheckUpdates = true,
    EngineType PreferredEngine = EngineType.Auto,
    string ClassicStrategyId = "general",
    string NextGenStrategyId = "nextgen-balanced",
    bool CheckChatGpt = false,
    bool CheckInstagram = false,
    bool CheckTikTok = false,
    bool CheckTelegram = false,
    ServiceSelection? Services = null);

public sealed record ServiceSelection(
    bool YouTube = true,
    bool Discord = true,
    bool DiscordVoice = true,
    bool ChatGpt = false,
    bool Instagram = false,
    bool TikTok = false,
    bool Telegram = false);

public sealed record EngineRuntimeOptions(
    IpSetMode IpSetMode = IpSetMode.Loaded,
    bool GameFilterEnabled = false,
    string GameFilterTcp = "1024-65535",
    string GameFilterUdp = "1024-65535",
    IReadOnlyList<string>? EnabledServiceIds = null,
    bool ReferenceCompatible = false,
    bool ClassicReferenceExact = false);

public sealed record UpdateManifest(
    string Version,
    string DownloadUrl,
    string Sha256,
    string ReleaseNotes,
    bool Mandatory = false,
    string? ClassicEngineVersion = null,
    string? NextGenEngineVersion = null,
    string? StrategyPackVersion = null);

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    Version CurrentVersion,
    Version LatestVersion,
    UpdateManifest Manifest);

public sealed record UpdateState(
    DateTimeOffset? LastCheckedAt = null,
    UpdateManifest? LatestManifest = null);

