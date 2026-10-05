using System.Diagnostics;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed class OptimizationEngine
{
    private readonly StrategyStore _strategies;
    private readonly IBypassEngine _engine;
    private readonly ConnectivityTester _tester;
    private readonly RotatingFileLogger _logger;

    public OptimizationEngine(StrategyStore strategies, IBypassEngine engine, ConnectivityTester tester, RotatingFileLogger logger)
    {
        _strategies = strategies;
        _engine = engine;
        _tester = tester;
        _logger = logger;
    }

    public async Task<OptimizationResult> OptimizeAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool checkYouTube = true,
        bool checkDiscord = true,
        bool checkVoice = true,
        EngineType preferredEngine = EngineType.Auto,
        bool deepSearch = false,
        EngineType firstAutoEngine = EngineType.Auto,
        IReadOnlyList<string>? priorityStrategyIds = null,
        IReadOnlyList<string>? deprioritizedStrategyIds = null,
        IReadOnlyCollection<string>? enabledTargetIds = null)
    {
        checkVoice &= checkDiscord;
        var enabledTargets = enabledTargetIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            ?? BuildEnabledTargets(checkYouTube, checkDiscord, checkVoice);
        if (enabledTargets.Length == 0)
        {
            throw new ArgumentException("Select at least one service to test during optimization.");
        }

        IReadOnlyList<DiagnosticResult> baseline = [];
        var allCandidates = (await _strategies.LoadAsync(cancellationToken).ConfigureAwait(false)).Strategies;
        var candidates = OrderCandidates(allCandidates, preferredEngine, deepSearch, firstAutoEngine, priorityStrategyIds, deprioritizedStrategyIds);
        var scores = new List<StrategyScore>();
        var testedStrategies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await _engine.StopAsync(ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var strategy = candidates[candidateIndex];
            if (!testedStrategies.Add(strategy.Id)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var displayName = StrategyNames.DisplayName(strategy);
            progress?.Report($"Testing {displayName} — {candidateIndex + 1}/{candidates.Count}");
            var startup = Stopwatch.StartNew();
            var keepRunning = false;
            try
            {
                var snapshot = await _engine.StartAsync(strategy.Id, ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
                startup.Stop();
                var results = await _tester.RunQuickHealthCheckAsync(enabledTargets, cancellationToken).ConfigureAwait(false);
                var score = StrategyScorer.Calculate(strategy.Id, results, startup.Elapsed, enabledTargets) with
                {
                    EngineStarted = snapshot.EngineRunning,
                    DriverActive = snapshot.DriverActive
                };
                scores.Add(score);
                var targetSummary = FormatTargets(score, enabledTargets);
                progress?.Report($"{displayName}: {targetSummary}; score={score.Score:F0}");
                await _logger.InfoAsync($"Strategy test {displayName}: engine=OK driver=OK {targetSummary} score={score.Score:F1}", cancellationToken).ConfigureAwait(false);
                if (TargetsMatch(score, enabledTargets, ServiceReachability.Working))
                {
                    keepRunning = true;
                    _engine.SetConnectivityVerified(true);
                    await _logger.SuccessAsync($"Fast optimization selected {strategy.Id} after {testedStrategies.Count} candidate(s).", cancellationToken).ConfigureAwait(false);
                    return new(true, snapshot.StrategyId, scores, baseline,
                        $"Selected {displayName} after {testedStrategies.Count}/{candidates.Count} checks.", FullyWorking: true);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                scores.Add(new(strategy.Id, 0, 0, 0, 1, 1, startup.Elapsed,
                    EngineStarted: false, DriverActive: false, FailureReason: exception.Message));
                progress?.Report($"{displayName}: ENGINE FAILED - {exception.Message}");
                await _logger.WarningAsync($"Strategy test failed: {strategy.Id}: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!keepRunning)
                {
                    await _engine.StopAsync(ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var ordered = scores.Where(score => score.EngineStarted && score.DriverActive)
            .OrderByDescending(score => TargetsMatch(score, enabledTargets, ServiceReachability.Working))
            .ThenByDescending(score => score.Score)
            .ThenBy(score => score.AverageLatencyMs)
            .ToArray();
        var best = ordered.FirstOrDefault();
        var reason = best is null
            ? "Ни одна стратегия не смогла запустить движок и WinDivert. Откройте диагностику для подробностей."
            : $"Полностью рабочая стратегия не найдена. Лучший результат: {StrategyNames.DisplayName(best.StrategyId)} — {FormatTargets(best, enabledTargets)}. "
                + (deepSearch ? "Можно использовать её вручную." : "Можно использовать её вручную или запустить расширенный поиск.");
        return new(false, best?.StrategyId, scores, baseline, reason, FullyWorking: false);
    }

    public static IReadOnlyList<Strategy> OrderCandidates(
        IReadOnlyList<Strategy> strategies,
        EngineType preferredEngine,
        bool deepSearch,
        EngineType firstAutoEngine = EngineType.Auto,
        IReadOnlyList<string>? priorityStrategyIds = null,
        IReadOnlyList<string>? deprioritizedStrategyIds = null)
    {
        IEnumerable<Strategy> filtered = preferredEngine == EngineType.Auto
            ? strategies
            : strategies.Where(strategy => strategy.EngineType == preferredEngine);
        var priority = (priorityStrategyIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        var failed = (deprioritizedStrategyIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        filtered = filtered
            .OrderBy(strategy => failed.Contains(strategy.Id))
            .ThenBy(strategy => priority.TryGetValue(strategy.Id, out var index) ? index : int.MaxValue);
        if (deepSearch) return filtered.ToArray();

        if (preferredEngine != EngineType.Auto) return filtered.Take(6).ToArray();
        var classic = filtered.Where(strategy => strategy.EngineType == EngineType.Classic).Take(3);
        var nextGen = filtered.Where(strategy => strategy.EngineType == EngineType.NextGen).Take(3);
        return firstAutoEngine == EngineType.NextGen
            ? nextGen.Concat(classic).Take(8).ToArray()
            : classic.Concat(nextGen).Take(8).ToArray();
    }

    private static string[] BuildEnabledTargets(bool checkYouTube, bool checkDiscord, bool checkVoice)
    {
        var result = new List<string>();
        if (checkYouTube) result.Add(ServiceTargetCatalog.YouTube);
        if (checkDiscord) result.Add(ServiceTargetCatalog.Discord);
        if (checkVoice) result.Add(ServiceTargetCatalog.Voice);
        return result.ToArray();
    }

    private static bool TargetsMatch(StrategyScore score, IEnumerable<string> enabledTargets, ServiceReachability expected) =>
        enabledTargets.All(id => score.TargetResults?.TryGetValue(id, out var state) == true && state == expected);

    private static string FormatTargets(StrategyScore score, IEnumerable<string> enabledTargets) => string.Join(", ",
        enabledTargets.Select(id => $"{ServiceTargetCatalog.Get(id).DisplayName}="
            + (score.TargetResults?.TryGetValue(id, out var state) == true ? state : ServiceReachability.NotTested)));

}

