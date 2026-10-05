using Rafferty.Shared;

namespace Rafferty.Core;

public sealed record RuntimeState(
    string? ActiveStrategyId,
    IReadOnlyList<string> BackupStrategyIds,
    DateTimeOffset? LastSuccessfulTest,
    string? LastNetworkId = null,
    Rafferty.Shared.EngineType ActiveEngine = Rafferty.Shared.EngineType.Auto,
    string? LastSuccessfulStrategy = null,
    Rafferty.Shared.EngineType LastSuccessfulEngine = Rafferty.Shared.EngineType.Auto,
    IReadOnlyList<StrategyHistoryEntry>? StrategyHistory = null,
    string? LastKnownGoodClassicStrategy = null,
    string? LastKnownGoodNextGenStrategy = null,
    IReadOnlyList<ServiceStrategyResult>? ServiceStrategies = null,
    string? LastKnownGoodCommand = null);

public sealed record StrategyHistoryEntry(
    string StrategyId,
    bool Success,
    DateTimeOffset LastTested);

