using Rafferty.Core;
using Rafferty.Shared;

namespace Rafferty.Service;

public sealed class CommandDispatcher
{
    private readonly IBypassEngine _engine;
    private readonly StrategyStore _strategies;
    private readonly ConnectivityTester _tester;
    private readonly OptimizationEngine _optimizer;
    private readonly RotatingFileLogger _logger;
    private readonly JsonFileStore<RuntimeState> _runtimeStore = new(AppPaths.RuntimeFile);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private ReachabilitySnapshot _reachability = ReachabilitySnapshot.Unknown;

    public CommandDispatcher(IBypassEngine engine, StrategyStore strategies, ConnectivityTester tester, OptimizationEngine optimizer, RotatingFileLogger logger)
    {
        _engine = engine;
        _strategies = strategies;
        _tester = tester;
        _optimizer = optimizer;
        _logger = logger;
    }

    public EngineSnapshot Status => _engine.Snapshot(_reachability);

    public async Task<PipeResponse> DispatchAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return request.Command switch
            {
                PipeCommand.Status => Ok("Status received.", request.RequestId),
                PipeCommand.Start => await StartAsync(request, cancellationToken).ConfigureAwait(false),
                PipeCommand.ApplyStrategy => await StartAsync(request, cancellationToken).ConfigureAwait(false),
                PipeCommand.Stop => new(true, "Engine stopped.", await _engine.StopAsync(_reachability, cancellationToken).ConfigureAwait(false), RequestId: request.RequestId),
                PipeCommand.Restart => new(true, "Engine restarted.", await _engine.RestartAsync(_reachability, cancellationToken).ConfigureAwait(false), RequestId: request.RequestId),
                PipeCommand.RunDiagnostics => await DiagnosticsAsync(request.RequestId, cancellationToken).ConfigureAwait(false),
                PipeCommand.TestStrategy => await TestStrategyAsync(request, cancellationToken).ConfigureAwait(false),
                PipeCommand.Optimize => await OptimizeAsync(request.RequestId, cancellationToken).ConfigureAwait(false),
                PipeCommand.ReloadLists => await ReloadAsync(request.RequestId, cancellationToken).ConfigureAwait(false),
                _ => new(false, "Unsupported command.", Status, RequestId: request.RequestId)
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _logger.ErrorAsync($"Command {request.Command} failed: {exception.Message}", cancellationToken).ConfigureAwait(false);
            return new(false, exception.Message, Status, RequestId: request.RequestId);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var runtime = await _runtimeStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(runtime?.ActiveStrategyId))
        {
            await _engine.StartAsync(runtime.ActiveStrategyId, _reachability, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var runtime = await _runtimeStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!_engine.CanAutoRestart())
        {
            await _logger.ErrorAsync("Engine restart suppressed after three crashes in 60 seconds.", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!string.IsNullOrWhiteSpace(runtime?.ActiveStrategyId))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            await _engine.StartAsync(runtime.ActiveStrategyId, _reachability, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task NetworkChangedAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        var diagnostics = await _tester.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        _reachability = ConnectivityTester.Summarize(diagnostics);
        await _logger.InfoAsync($"Network changed. id={NetworkIdentity.GetCurrent()}", cancellationToken).ConfigureAwait(false);
    }

    private async Task<PipeResponse> StartAsync(PipeRequest request, CancellationToken token)
    {
        var strategyId = request.StrategyId;
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            strategyId = (await _runtimeStore.LoadAsync(token).ConfigureAwait(false))?.ActiveStrategyId;
        }
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            strategyId = (await _strategies.LoadAsync(token).ConfigureAwait(false)).Strategies.First().Id;
        }
        var status = await _engine.StartAsync(strategyId, _reachability, token).ConfigureAwait(false);
        await SaveRuntimeAsync(strategyId, [], token).ConfigureAwait(false);
        return new(true, $"Strategy {strategyId} applied.", status, RequestId: request.RequestId);
    }

    private async Task<PipeResponse> DiagnosticsAsync(string? requestId, CancellationToken token)
    {
        var diagnostics = await _tester.RunDiagnosticsAsync(token).ConfigureAwait(false);
        _reachability = ConnectivityTester.Summarize(diagnostics);
        return new(true, "Diagnostics completed.", Status, diagnostics, RequestId: requestId);
    }

    private async Task<PipeResponse> TestStrategyAsync(PipeRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.StrategyId)) throw new ArgumentException("Strategy id is required.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await _engine.StartAsync(request.StrategyId, _reachability, token).ConfigureAwait(false);
        await Task.Delay(900, token).ConfigureAwait(false);
        timer.Stop();
        var diagnostics = await _tester.RunDiagnosticsAsync(token).ConfigureAwait(false);
        var score = StrategyScorer.Calculate(request.StrategyId, diagnostics, timer.Elapsed);
        _reachability = ConnectivityTester.Summarize(diagnostics);
        return new(true, $"Strategy score: {score.Score:F1}.", Status, diagnostics, new(true, request.StrategyId, [score], diagnostics, "Test completed."), request.RequestId);
    }

    private async Task<PipeResponse> OptimizeAsync(string? requestId, CancellationToken token)
    {
        var result = await _optimizer.OptimizeAsync(cancellationToken: token).ConfigureAwait(false);
        if (result.Success && result.SelectedStrategyId is not null)
        {
            var backups = result.Scores.Where(score => score.StrategyId != result.SelectedStrategyId).OrderByDescending(score => score.Score).Take(3).Select(score => score.StrategyId).ToArray();
            await SaveRuntimeAsync(result.SelectedStrategyId, backups, token).ConfigureAwait(false);
            var finalDiagnostics = await _tester.RunDiagnosticsAsync(token).ConfigureAwait(false);
            _reachability = ConnectivityTester.Summarize(finalDiagnostics);
        }
        return new(result.Success, result.Message, Status, result.Baseline, result, requestId);
    }

    private Task<PipeResponse> ReloadAsync(string? requestId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _strategies.Invalidate();
        return Task.FromResult(new PipeResponse(true, "Lists and strategies will be reloaded.", Status, RequestId: requestId));
    }

    private PipeResponse Ok(string message, string? requestId) => new(true, message, Status, RequestId: requestId);

    private Task SaveRuntimeAsync(string strategyId, IReadOnlyList<string> backups, CancellationToken token) =>
        _runtimeStore.SaveAsync(new(strategyId, backups, DateTimeOffset.Now, NetworkIdentity.GetCurrent(), Status.EngineType), token);
}

