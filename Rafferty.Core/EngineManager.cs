using System.Diagnostics;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed class EngineManager : IAsyncDisposable
{
    private readonly StrategyStore _strategies;
    private readonly RotatingFileLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _crashes = new();
    private readonly WindowsJob _job = new();
    private Process? _process;
    private Strategy? _strategy;
    private DateTimeOffset? _startedAt;
    private string? _lastError;
    private string? _lastCommandLine;
    private bool _driverActive;
    private bool _strategyApplied;
    private bool _connectivityVerified;
    private bool _stopping;

    public EngineManager(StrategyStore strategies, RotatingFileLogger logger)
    {
        _strategies = strategies;
        _logger = logger;
    }

    public event EventHandler? UnexpectedExit;

    public EngineSnapshot Snapshot(ReachabilitySnapshot reachability) => new(
        ServiceOnline: true,
        EngineRunning: _process is { HasExited: false },
        ProcessId: _process is { HasExited: false } process ? process.Id : null,
        StrategyId: _strategy?.Id,
        StartedAt: _startedAt,
        LastError: _lastError,
        Reachability: reachability,
        CrashCount: _crashes.Count,
        IsAdministrator: WindowsNetworkPrerequisites.IsAdministrator(),
        DriverActive: _driverActive,
        StrategyApplied: _strategyApplied,
        ConnectivityVerified: _connectivityVerified,
        CommandLine: _lastCommandLine);

    public void SetConnectivityVerified(bool verified) => _connectivityVerified = verified;

    public async Task<string> GetCommandLineAsync(string strategyId, bool sanitizePaths = false, CancellationToken token = default)
    {
        var strategy = await _strategies.GetAsync(strategyId, token).ConfigureAwait(false);
        var command = FormatCommandLine(AppPaths.EngineExecutable, strategy.Arguments.Select(ExpandArgument));
        return sanitizePaths
            ? command.Replace(AppPaths.EngineDirectory, "{runtime}\\engine", StringComparison.OrdinalIgnoreCase)
                .Replace(AppPaths.ListsDirectory, "{runtime}\\lists", StringComparison.OrdinalIgnoreCase)
            : command;
    }

    public async Task<EngineSnapshot> StartAsync(string strategyId, ReachabilitySnapshot reachability, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                if (string.Equals(_strategy?.Id, strategyId, StringComparison.OrdinalIgnoreCase))
                {
                    return Snapshot(reachability);
                }
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            var strategy = await _strategies.GetAsync(strategyId, cancellationToken).ConfigureAwait(false);
            if (!WindowsNetworkPrerequisites.IsAdministrator())
            {
                throw new UnauthorizedAccessException("Administrator rights are required for the network filter.");
            }
            if (!await WindowsNetworkPrerequisites.IsBaseFilteringEngineRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Windows Base Filtering Engine (BFE) is not running.");
            }
            var executable = ResolveEngineExecutable();
            await EngineIntegrity.VerifyAsync(AppPaths.EngineDirectory, cancellationToken).ConfigureAwait(false);
            await _logger.InfoAsync(await WindowsNetworkPrerequisites.EnsureTcpTimestampsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var expandedArguments = strategy.Arguments.Select(ExpandArgument).ToArray();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppPaths.EngineDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in expandedArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _lastCommandLine = FormatCommandLine(executable, expandedArguments);
            await _logger.InfoAsync($"Engine start command: {_lastCommandLine}", cancellationToken).ConfigureAwait(false);

            _stopping = false;
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => QueueEngineLog("INFO", args.Data);
            process.ErrorDataReceived += (_, args) => QueueEngineLog("ERROR", args.Data);
            process.Exited += HandleProcessExit;
            if (!process.Start())
            {
                throw new InvalidOperationException("winws did not start.");
            }
            _job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            _strategy = strategy;
            _startedAt = DateTimeOffset.Now;
            _lastError = null;
            _strategyApplied = false;
            _driverActive = false;
            _connectivityVerified = false;

            // winws reports invalid/missing strategy resources by exiting at once.
            // Do not return a misleading successful state to the UI in that case.
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                throw new InvalidOperationException($"winws failed during startup (exit code {process.ExitCode}). Check engine.log.");
            }
            _driverActive = await WindowsNetworkPrerequisites.IsWinDivertRunningAsync(cancellationToken).ConfigureAwait(false);
            if (!_driverActive)
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("winws is running but the WinDivert driver is not active.");
            }
            _strategyApplied = expandedArguments.Count(argument => argument == "--new") == strategy.Arguments.Count(argument => argument == "--new");

            await _logger.SuccessAsync($"Engine started. pid={process.Id} strategy={strategy.Id} driver=active rules={expandedArguments.Length} sections={expandedArguments.Count(argument => argument == "--new") + 1}", cancellationToken).ConfigureAwait(false);
            return Snapshot(reachability);
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            await _logger.ErrorAsync($"Engine start failed: {exception.Message}", cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EngineSnapshot> StopAsync(ReachabilitySnapshot reachability, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            return Snapshot(reachability);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EngineSnapshot> RestartAsync(ReachabilitySnapshot reachability, CancellationToken cancellationToken = default)
    {
        var strategyId = _strategy?.Id ?? throw new InvalidOperationException("No strategy is selected.");
        await StopAsync(reachability, cancellationToken).ConfigureAwait(false);
        return await StartAsync(strategyId, reachability, cancellationToken).ConfigureAwait(false);
    }

    public bool CanAutoRestart()
    {
        var cutoff = DateTimeOffset.Now.AddSeconds(-60);
        while (_crashes.TryPeek(out var crash) && crash < cutoff)
        {
            _crashes.Dequeue();
        }
        return _crashes.Count < 3;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            return;
        }

        _stopping = true;
        if (!_process.HasExited)
        {
            _process.Kill(true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        _process.Dispose();
        _process = null;
        _startedAt = null;
        _driverActive = false;
        _strategyApplied = false;
        _connectivityVerified = false;
        await _logger.InfoAsync("Engine stopped.", cancellationToken).ConfigureAwait(false);
    }

    private string ResolveEngineExecutable()
    {
        if (!File.Exists(AppPaths.EngineExecutable))
        {
            throw new FileNotFoundException("winws.exe is not installed.", AppPaths.EngineExecutable);
        }

        var root = Path.GetFullPath(AppPaths.EngineDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var executable = Path.GetFullPath(AppPaths.EngineExecutable);
        if (!executable.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFileName(executable), "winws.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsafe engine executable path.");
        }
        return executable;
    }

    private static string ExpandArgument(string argument) => argument
        .Replace("{engine}", AppPaths.EngineDirectory, StringComparison.OrdinalIgnoreCase)
        .Replace("{lists}", AppPaths.ListsDirectory, StringComparison.OrdinalIgnoreCase);

    private void HandleProcessExit(object? sender, EventArgs eventArgs)
    {
        if (_stopping)
        {
            return;
        }
        _crashes.Enqueue(DateTimeOffset.Now);
        _lastError = $"winws exited unexpectedly with code {_process?.ExitCode}.";
        _driverActive = false;
        _strategyApplied = false;
        _connectivityVerified = false;
        QueueEngineLog("ERROR", _lastError);
        UnexpectedExit?.Invoke(this, EventArgs.Empty);
    }

    private void QueueEngineLog(string level, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        _ = level == "ERROR" ? _logger.ErrorAsync($"winws: {message}") : _logger.InfoAsync($"winws: {message}");
    }

    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
        : value;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(ReachabilitySnapshot.Unknown).ConfigureAwait(false);
        _job.Dispose();
        _gate.Dispose();
    }
}

