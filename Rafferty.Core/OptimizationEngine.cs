using System.Diagnostics;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed class OptimizationEngine
{
    private readonly StrategyStore _strategies;
    private readonly EngineManager _engine;
    private readonly ConnectivityTester _tester;
    private readonly RotatingFileLogger _logger;

    public OptimizationEngine(StrategyStore strategies, EngineManager engine, ConnectivityTester tester, RotatingFileLogger logger)
    {
        _strategies = strategies;
        _engine = engine;
        _tester = tester;
        _logger = logger;
    }

    public async Task<OptimizationResult> OptimizeAsync(CancellationToken cancellationToken = default)
    {
        var baseline = await _tester.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await SelectCandidatesAsync(baseline, cancellationToken).ConfigureAwait(false);
        var scores = new List<StrategyScore>();

        await _engine.StopAsync(ConnectivityTester.Summarize(baseline), cancellationToken).ConfigureAwait(false);
        foreach (var strategy in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startup = Stopwatch.StartNew();
            try
            {
                await _engine.StartAsync(strategy.Id, ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
                startup.Stop();
                var results = await _tester.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                scores.Add(StrategyScorer.Calculate(strategy.Id, results, startup.Elapsed));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                scores.Add(new(strategy.Id, 0, 0, 0, 1, 1, startup.Elapsed));
                await _logger.WarningAsync($"Strategy test failed: {strategy.Id}: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _engine.StopAsync(ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
            }
        }

        var best = scores.OrderByDescending(score => score.Score).ThenBy(score => score.AverageLatencyMs).FirstOrDefault();
        if (best is null || best.SuccessRate < 0.5)
        {
            return new(false, null, scores, baseline, "No stable strategy passed the required checks.");
        }

        var selected = await _engine.StartAsync(best.StrategyId, ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
        var verification = await _tester.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        _engine.SetConnectivityVerified(MinimumChecksPassed(verification));
        await _logger.SuccessAsync($"Optimization selected {best.StrategyId} with score {best.Score}.", cancellationToken).ConfigureAwait(false);
        return new(true, selected.StrategyId, scores, baseline, $"Selected {best.StrategyId} (score {best.Score:F1}).");
    }

    private static bool MinimumChecksPassed(IReadOnlyList<DiagnosticResult> results)
    {
        string[] required = ["youtube", "googlevideo", "discord-api", "discord-cdn", "discord-gateway", "discord-stun"];
        return required.All(id => results.Any(result => result.Id == id && result.State == DiagnosticState.Success));
    }

    private async Task<IReadOnlyList<Strategy>> SelectCandidatesAsync(IReadOnlyList<DiagnosticResult> baseline, CancellationToken token)
    {
        var database = await _strategies.LoadAsync(token).ConfigureAwait(false);
        var failed = baseline.Where(result => result.State == DiagnosticState.Error).Select(result => result.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var needsUdp = failed.Contains("discord-stun");
        var needsYouTube = failed.Contains("youtube") || failed.Contains("googlevideo");
        var needsDiscord = failed.Contains("discord-api") || failed.Contains("discord-cdn");

        var selected = database.Strategies.Where(strategy =>
            (needsUdp && strategy.Protocols.Contains("udp", StringComparer.OrdinalIgnoreCase)) ||
            (needsYouTube && strategy.Targets.Contains("youtube", StringComparer.OrdinalIgnoreCase)) ||
            (needsDiscord && strategy.Targets.Any(target => target.StartsWith("discord", StringComparison.OrdinalIgnoreCase)))).ToArray();
        return selected.Length > 0 ? selected : database.Strategies.Take(4).ToArray();
    }
}

