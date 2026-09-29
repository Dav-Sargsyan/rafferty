using System.Text.Json.Serialization;

namespace Rafferty.Shared;

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
    string? CommandLine = null);

public sealed record ReachabilitySnapshot(
    ServiceReachability Internet,
    ServiceReachability YouTube,
    ServiceReachability Discord,
    ServiceReachability DiscordVoice,
    DateTimeOffset? CheckedAt = null)
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

public sealed record DiagnosticResult(
    string Id,
    string Name,
    DiagnosticState State,
    string Detail,
    double? LatencyMs = null);

public sealed record StrategyScore(
    string StrategyId,
    double Score,
    double SuccessRate,
    double AverageLatencyMs,
    double PacketLoss,
    int Errors,
    TimeSpan StartupTime);

public sealed record OptimizationResult(
    bool Success,
    string? SelectedStrategyId,
    IReadOnlyList<StrategyScore> Scores,
    IReadOnlyList<DiagnosticResult> Baseline,
    string Message);

public sealed record UserSettings(
    bool StartWithWindows = false,
    bool StartMinimized = false,
    bool AutoOptimizeOnNewNetwork = true,
    bool AutoRecovery = true,
    bool Notifications = true,
    bool AdvancedMode = false,
    string Theme = "Dark",
    bool StartEnabled = false,
    string Language = "ru-RU");

