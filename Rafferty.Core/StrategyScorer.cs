using Rafferty.Shared;

namespace Rafferty.Core;

public static class StrategyScorer
{
    public static StrategyScore Calculate(
        string strategyId,
        IReadOnlyList<DiagnosticResult> results,
        TimeSpan startupTime,
        bool checkYouTube = true,
        bool checkDiscord = true,
        bool checkVoice = true)
    {
        var enabledTargets = new List<string>();
        if (checkYouTube) enabledTargets.Add(ServiceTargetCatalog.YouTube);
        if (checkDiscord) enabledTargets.Add(ServiceTargetCatalog.Discord);
        if (checkVoice) enabledTargets.Add(ServiceTargetCatalog.Voice);
        return Calculate(strategyId, results, startupTime, enabledTargets);
    }

    public static StrategyScore Calculate(
        string strategyId,
        IReadOnlyList<DiagnosticResult> results,
        TimeSpan startupTime,
        IReadOnlyCollection<string> enabledTargetIds)
    {
        if (results.Count == 0 || enabledTargetIds.Count == 0)
        {
            return new(strategyId, 0, 0, 0, 1, 1, startupTime,
                FailureReason: "No diagnostic results were returned.");
        }

        var targetResults = ServiceTargetCatalog.SummarizeTargets(results, enabledTargetIds);
        var successes = results.Count(result => result.State == DiagnosticState.Success);
        var errors = results.Count(result => result.State == DiagnosticState.Error);
        var successRate = successes / (double)results.Count;
        var latencies = results.Where(result => result.LatencyMs.HasValue).Select(result => result.LatencyMs!.Value).ToArray();
        var averageLatency = latencies.Length == 0 ? 1000 : latencies.Average();
        var packetLoss = errors / (double)results.Count;
        var startupScore = Math.Clamp(1 - startupTime.TotalMilliseconds / 5000d, 0, 1);
        var targetScore = targetResults.Values.Average(ReachabilityValue);
        var score = (targetScore * 95) + (startupScore * 5);
        ServiceReachability Target(string id) => targetResults.TryGetValue(id, out var state) ? state : ServiceReachability.NotTested;
        return new(strategyId, Math.Round(score, 2), successRate, Math.Round(averageLatency, 2), packetLoss, errors, startupTime,
            Target(ServiceTargetCatalog.YouTube), Target(ServiceTargetCatalog.Discord), Target(ServiceTargetCatalog.Voice),
            TargetResults: targetResults);
    }

    private static double ReachabilityValue(ServiceReachability state) => state switch
    {
        ServiceReachability.Working => 1,
        ServiceReachability.Degraded => 0.5,
        _ => 0
    };
}
