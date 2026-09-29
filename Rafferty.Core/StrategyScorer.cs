using Rafferty.Shared;

namespace Rafferty.Core;

public static class StrategyScorer
{
    public static StrategyScore Calculate(string strategyId, IReadOnlyList<DiagnosticResult> results, TimeSpan startupTime)
    {
        if (results.Count == 0)
        {
            return new(strategyId, 0, 0, 0, 1, 1, startupTime);
        }

        var successes = results.Count(result => result.State == DiagnosticState.Success);
        var errors = results.Count(result => result.State == DiagnosticState.Error);
        var successRate = successes / (double)results.Count;
        var latencies = results.Where(result => result.LatencyMs.HasValue).Select(result => result.LatencyMs!.Value).ToArray();
        var averageLatency = latencies.Length == 0 ? 1000 : latencies.Average();
        var packetLoss = errors / (double)results.Count;
        var latencyScore = Math.Clamp(1 - averageLatency / 1000d, 0, 1);
        var startupScore = Math.Clamp(1 - startupTime.TotalMilliseconds / 5000d, 0, 1);
        var score = (successRate * 70) + (latencyScore * 15) + ((1 - packetLoss) * 10) + (startupScore * 5);
        return new(strategyId, Math.Round(score, 2), successRate, Math.Round(averageLatency, 2), packetLoss, errors, startupTime);
    }
}
