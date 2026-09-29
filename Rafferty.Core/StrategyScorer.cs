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
        if (results.Count == 0)
        {
            return new(strategyId, 0, 0, 0, 1, 1, startupTime,
                FailureReason: "No diagnostic results were returned.");
        }

        var reachability = ConnectivityTester.Summarize(results);
        var successes = results.Count(result => result.State == DiagnosticState.Success);
        var errors = results.Count(result => result.State == DiagnosticState.Error);
        var successRate = successes / (double)results.Count;
        var latencies = results.Where(result => result.LatencyMs.HasValue).Select(result => result.LatencyMs!.Value).ToArray();
        var averageLatency = latencies.Length == 0 ? 1000 : latencies.Average();
        var packetLoss = errors / (double)results.Count;
        var startupScore = Math.Clamp(1 - startupTime.TotalMilliseconds / 5000d, 0, 1);
        var serviceWeight = (checkYouTube ? 40d : 0d) + (checkDiscord ? 40d : 0d) + (checkVoice ? 15d : 0d);
        var weightedScore = (checkYouTube ? ReachabilityValue(reachability.YouTube) * 40 : 0)
            + (checkDiscord ? ReachabilityValue(reachability.Discord) * 40 : 0)
            + (checkVoice ? ReachabilityValue(reachability.DiscordVoice) * 15 : 0)
            + (startupScore * 5);
        var score = weightedScore / (serviceWeight + 5) * 100;
        return new(strategyId, Math.Round(score, 2), successRate, Math.Round(averageLatency, 2), packetLoss, errors, startupTime,
            reachability.YouTube, reachability.Discord, reachability.DiscordVoice);
    }

    private static double ReachabilityValue(ServiceReachability state) => state switch
    {
        ServiceReachability.Working => 1,
        ServiceReachability.Degraded => 0.5,
        _ => 0
    };
}
