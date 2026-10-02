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
        IReadOnlyList<string>? priorityStrategyIds = null)
    {
        if (!checkYouTube && !checkDiscord && !checkVoice)
        {
            throw new ArgumentException("Select at least one service to test during optimization.");
        }

        var baseline = await _tester.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        var allCandidates = (await _strategies.LoadAsync(cancellationToken).ConfigureAwait(false)).Strategies;
        var candidates = OrderCandidates(allCandidates, preferredEngine, deepSearch, firstAutoEngine, priorityStrategyIds);
        var scores = new List<StrategyScore>();
        var workingCandidates = 0;

        await _engine.StopAsync(ConnectivityTester.Summarize(baseline), cancellationToken).ConfigureAwait(false);
        foreach (var strategy in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var displayName = StrategyNames.DisplayName(strategy);
            progress?.Report($"Testing {displayName}...");
            var startup = Stopwatch.StartNew();
            try
            {
                var snapshot = await _engine.StartAsync(strategy.Id, ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
                startup.Stop();
                var results = await _tester.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                var score = StrategyScorer.Calculate(strategy.Id, results, startup.Elapsed, checkYouTube, checkDiscord, checkVoice) with
                {
                    EngineStarted = snapshot.EngineRunning,
                    DriverActive = snapshot.DriverActive
                };
                scores.Add(score);
                progress?.Report($"{displayName}: YouTube={score.YouTube}, Discord={score.Discord}, Voice={score.Voice}, score={score.Score:F0}");
                await _logger.InfoAsync($"Strategy test {displayName}: engine=OK driver=OK YouTube={score.YouTube} Discord={score.Discord} Voice={score.Voice} score={score.Score:F1}", cancellationToken).ConfigureAwait(false);
                if (RequiredServicesMatch(score, ServiceReachability.Working, checkYouTube, checkDiscord, checkVoice))
                {
                    workingCandidates++;
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
                await _engine.StopAsync(ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            if (workingCandidates >= 3)
            {
                progress?.Report("Three working strategies found; selecting the best result.");
                break;
            }
        }

        var ordered = scores.Where(score => score.EngineStarted && score.DriverActive)
            .OrderByDescending(score => RequiredServicesMatch(score, ServiceReachability.Working, checkYouTube, checkDiscord, checkVoice))
            .ThenByDescending(score => score.Score)
            .ThenBy(score => score.AverageLatencyMs)
            .ToArray();
        var best = ordered.FirstOrDefault();
        if (best is null || RequiredServiceUnavailable(best, checkYouTube, checkDiscord, checkVoice))
        {
            var reason = best is null
                ? "Every strategy failed to start the engine or WinDivert."
                : $"No strategy restored every selected service. Best result: {StrategyNames.DisplayName(best.StrategyId)}; YouTube={best.YouTube}; Discord={best.Discord}; Voice={best.Voice}.";
            return new(false, best?.StrategyId, scores, baseline, reason);
        }

        progress?.Report($"Applying {StrategyNames.DisplayName(best.StrategyId)}...");
        var selected = await _engine.StartAsync(best.StrategyId, ReachabilitySnapshot.Unknown, cancellationToken).ConfigureAwait(false);
        var verification = await _tester.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        _engine.SetConnectivityVerified(SelectedServicesWorking(verification, checkYouTube, checkDiscord, checkVoice));
        await _logger.SuccessAsync($"Optimization selected {best.StrategyId} with score {best.Score}.", cancellationToken).ConfigureAwait(false);
        return new(true, selected.StrategyId, scores, baseline,
            $"Selected {StrategyNames.DisplayName(best.StrategyId)} (YouTube={best.YouTube}, Discord={best.Discord}, Voice={best.Voice}, score={best.Score:F1}).");
    }

    public static IReadOnlyList<Strategy> OrderCandidates(
        IReadOnlyList<Strategy> strategies,
        EngineType preferredEngine,
        bool deepSearch,
        EngineType firstAutoEngine = EngineType.Auto,
        IReadOnlyList<string>? priorityStrategyIds = null)
    {
        IEnumerable<Strategy> filtered = preferredEngine == EngineType.Auto
            ? strategies
            : strategies.Where(strategy => strategy.EngineType == preferredEngine);
        var priority = (priorityStrategyIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        filtered = filtered.OrderBy(strategy => priority.TryGetValue(strategy.Id, out var index) ? index : int.MaxValue);
        if (deepSearch) return filtered.ToArray();

        if (preferredEngine != EngineType.Auto) return filtered.Take(6).ToArray();
        var classic = filtered.Where(strategy => strategy.EngineType == EngineType.Classic).Take(3);
        var nextGen = filtered.Where(strategy => strategy.EngineType == EngineType.NextGen).Take(3);
        return firstAutoEngine == EngineType.NextGen
            ? nextGen.Concat(classic).Take(8).ToArray()
            : classic.Concat(nextGen).Take(8).ToArray();
    }

    private static bool SelectedServicesWorking(IReadOnlyList<DiagnosticResult> results, bool checkYouTube, bool checkDiscord, bool checkVoice)
    {
        var summary = ConnectivityTester.Summarize(results);
        return (!checkYouTube || summary.YouTube == ServiceReachability.Working)
            && (!checkDiscord || summary.Discord == ServiceReachability.Working)
            && (!checkVoice || summary.DiscordVoice == ServiceReachability.Working);
    }

    private static bool RequiredServicesMatch(StrategyScore score, ServiceReachability expected, bool checkYouTube, bool checkDiscord, bool checkVoice) =>
        (!checkYouTube || score.YouTube == expected)
        && (!checkDiscord || score.Discord == expected)
        && (!checkVoice || score.Voice == expected);

    private static bool RequiredServiceUnavailable(StrategyScore score, bool checkYouTube, bool checkDiscord, bool checkVoice) =>
        (checkYouTube && score.YouTube == ServiceReachability.Unavailable)
        || (checkDiscord && score.Discord == ServiceReachability.Unavailable)
        || (checkVoice && score.Voice == ServiceReachability.Unavailable);

}

