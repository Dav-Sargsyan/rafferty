namespace Rafferty.Core;

public sealed record RuntimeState(
    string? ActiveStrategyId,
    IReadOnlyList<string> BackupStrategyIds,
    DateTimeOffset? LastSuccessfulTest,
    string? LastNetworkId = null,
    Rafferty.Shared.EngineType ActiveEngine = Rafferty.Shared.EngineType.Auto);

