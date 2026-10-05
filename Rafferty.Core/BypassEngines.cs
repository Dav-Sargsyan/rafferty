using Rafferty.Shared;

namespace Rafferty.Core;

public sealed class ClassicBypassEngine(StrategyStore strategies, RotatingFileLogger logger)
    : EngineManager(strategies, logger, EngineType.Classic, AppPaths.ClassicEngineDirectory, AppPaths.ClassicEngineExecutable, "winws.exe");

public sealed class NextGenBypassEngine(StrategyStore strategies, RotatingFileLogger logger)
    : EngineManager(strategies, logger, EngineType.NextGen, AppPaths.NextGenEngineDirectory, AppPaths.NextGenEngineExecutable, "winws2.exe");

public sealed class BypassEngineManager : IBypassEngine
{
    private readonly StrategyStore _strategies;
    private readonly ClassicBypassEngine _classic;
    private readonly NextGenBypassEngine _nextGen;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private IBypassEngine? _active;

    public BypassEngineManager(StrategyStore strategies, RotatingFileLogger logger)
    {
        _strategies = strategies;
        _classic = new(strategies, logger);
        _nextGen = new(strategies, logger);
        _classic.UnexpectedExit += ForwardUnexpectedExit;
        _nextGen.UnexpectedExit += ForwardUnexpectedExit;
    }

    public event EventHandler? UnexpectedExit;
    public EngineType EngineType => _active?.EngineType ?? EngineType.Auto;
    public string WorkingDirectory => _active?.WorkingDirectory ?? AppPaths.RuntimeRoot;
    public EngineRuntimeOptions RuntimeOptions { get; private set; } = new();

    public void SetRuntimeOptions(EngineRuntimeOptions options)
    {
        RuntimeOptions = options;
        _classic.SetRuntimeOptions(options);
        _nextGen.SetRuntimeOptions(options);
    }

    public EngineSnapshot Snapshot(ReachabilitySnapshot reachability) =>
        _active?.Snapshot(reachability) ?? new(true, false, null, null, null, null, reachability, IsAdministrator: WindowsNetworkPrerequisites.IsAdministrator(), EngineType: EngineType.Auto);

    public void SetConnectivityVerified(bool verified) => _active?.SetConnectivityVerified(verified);

    public async Task<string> GetCommandLineAsync(string strategyId, bool sanitizePaths = false, CancellationToken token = default)
    {
        var engine = await ResolveAsync(strategyId, token).ConfigureAwait(false);
        return await engine.GetCommandLineAsync(strategyId, sanitizePaths, token).ConfigureAwait(false);
    }

    public async Task<EngineSnapshot> StartAsync(string strategyId, ReachabilitySnapshot reachability, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = await ResolveAsync(strategyId, cancellationToken).ConfigureAwait(false);
            var other = ReferenceEquals(target, _classic) ? (IBypassEngine)_nextGen : _classic;
            await other.StopAsync(reachability, cancellationToken).ConfigureAwait(false);
            if (_active is not null && !ReferenceEquals(_active, target))
                await _active.StopAsync(reachability, cancellationToken).ConfigureAwait(false);
            _active = target;
            return await target.StartAsync(strategyId, reachability, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<EngineSnapshot> StopAsync(ReachabilitySnapshot reachability, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _classic.StopAsync(reachability, cancellationToken).ConfigureAwait(false);
            await _nextGen.StopAsync(reachability, cancellationToken).ConfigureAwait(false);
            return Snapshot(reachability);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<EngineSnapshot> RestartAsync(ReachabilitySnapshot reachability, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active is null) throw new InvalidOperationException("No strategy is selected.");
            return await _active.RestartAsync(reachability, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool CanAutoRestart() => _active?.CanAutoRestart() ?? true;

    private async Task<IBypassEngine> ResolveAsync(string strategyId, CancellationToken token)
    {
        var strategy = await _strategies.GetAsync(strategyId, token).ConfigureAwait(false);
        return strategy.EngineType switch
        {
            EngineType.Classic => _classic,
            EngineType.NextGen => _nextGen,
            _ => throw new InvalidOperationException("A strategy cannot use the automatic engine type.")
        };
    }

    private void ForwardUnexpectedExit(object? sender, EventArgs args) => UnexpectedExit?.Invoke(this, args);

    public async ValueTask DisposeAsync()
    {
        await _classic.DisposeAsync().ConfigureAwait(false);
        await _nextGen.DisposeAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }
}
