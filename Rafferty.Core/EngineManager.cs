using System.Diagnostics;
using System.Text;
using Rafferty.Shared;

namespace Rafferty.Core;

public interface IBypassEngine : IAsyncDisposable
{
    event EventHandler? UnexpectedExit;
    EngineType EngineType { get; }
    string WorkingDirectory { get; }
    EngineRuntimeOptions RuntimeOptions { get; }
    void SetRuntimeOptions(EngineRuntimeOptions options);
    EngineSnapshot Snapshot(ReachabilitySnapshot reachability);
    void SetConnectivityVerified(bool verified);
    Task<string> GetCommandLineAsync(string strategyId, bool sanitizePaths = false, CancellationToken token = default);
    Task<EngineSnapshot> StartAsync(string strategyId, ReachabilitySnapshot reachability, CancellationToken cancellationToken = default);
    Task<EngineSnapshot> StopAsync(ReachabilitySnapshot reachability, CancellationToken cancellationToken = default);
    Task<EngineSnapshot> RestartAsync(ReachabilitySnapshot reachability, CancellationToken cancellationToken = default);
    bool CanAutoRestart();
}

public class EngineManager : IBypassEngine
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
    private EngineRuntimeOptions _runtimeOptions = new();
    private readonly EngineType _engineType;
    private readonly string _workingDirectory;
    private readonly string _executablePath;
    private readonly string _executableName;

    public EngineManager(StrategyStore strategies, RotatingFileLogger logger)
        : this(strategies, logger, EngineType.Classic, AppPaths.ClassicEngineDirectory, AppPaths.ClassicEngineExecutable, "winws.exe")
    {
    }

    protected EngineManager(StrategyStore strategies, RotatingFileLogger logger, EngineType engineType, string workingDirectory, string executablePath, string executableName)
    {
        _strategies = strategies;
        _logger = logger;
        _engineType = engineType;
        _workingDirectory = workingDirectory;
        _executablePath = executablePath;
        _executableName = executableName;
    }

    public event EventHandler? UnexpectedExit;

    public EngineType EngineType => _engineType;
    public string WorkingDirectory => _workingDirectory;
    public EngineRuntimeOptions RuntimeOptions => _runtimeOptions;

    public void SetRuntimeOptions(EngineRuntimeOptions options) => _runtimeOptions = options;

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
        CommandLine: _lastCommandLine,
        EngineType: _engineType);

    public void SetConnectivityVerified(bool verified) => _connectivityVerified = verified;

    public async Task<string> GetCommandLineAsync(string strategyId, bool sanitizePaths = false, CancellationToken token = default)
    {
        var strategy = await _strategies.GetAsync(strategyId, token).ConfigureAwait(false);
        EnsureCompatible(strategy);
        var command = FormatCommandLine(_executablePath, BuildArguments(strategy));
        return sanitizePaths
            ? command.Replace(_workingDirectory, $"{{runtime}}\\{_engineType.ToString().ToLowerInvariant()}", StringComparison.OrdinalIgnoreCase)
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
            EnsureCompatible(strategy);
            if (!WindowsNetworkPrerequisites.IsAdministrator())
            {
                throw new UnauthorizedAccessException("Administrator rights are required for the network filter.");
            }
            if (!await WindowsNetworkPrerequisites.IsBaseFilteringEngineRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Windows Base Filtering Engine (BFE) is not running.");
            }
            var executable = ResolveEngineExecutable();
            await EngineIntegrity.VerifyAsync(_workingDirectory, cancellationToken).ConfigureAwait(false);
            await _logger.InfoAsync(await WindowsNetworkPrerequisites.EnsureTcpTimestampsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var expandedArguments = BuildArguments(strategy).ToArray();
            ValidateReferencedFiles(expandedArguments, strategy);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = _workingDirectory,
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
            var startupReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) =>
            {
                QueueEngineLog("INFO", args.Data);
                if (IsStartupReadyMessage(args.Data)) startupReady.TrySetResult();
            };
            process.ErrorDataReceived += (_, args) =>
            {
                QueueEngineLog("ERROR", args.Data);
                if (IsStartupReadyMessage(args.Data)) startupReady.TrySetResult();
            };
            process.Exited += HandleProcessExit;
            process.Exited += (_, _) => startupReady.TrySetResult();
            if (!process.Start())
            {
                throw new InvalidOperationException($"{_executableName} did not start.");
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

            // Prefer winws' own capture-ready signal. The timeout covers builds that do not
            // print it, after which process and driver state are still verified explicitly.
            await Task.WhenAny(startupReady.Task, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false);
            if (process.HasExited)
            {
                throw new InvalidOperationException($"{_executableName} failed during startup (exit code {process.ExitCode}). Check engine.log.");
            }
            _driverActive = await WindowsNetworkPrerequisites.IsWinDivertRunningAsync(cancellationToken).ConfigureAwait(false);
            if (!_driverActive)
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"{_executableName} is running but the WinDivert driver is not active.");
            }
            _strategyApplied = true;

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
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
    }

    private string ResolveEngineExecutable()
    {
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException($"{_executableName} is not installed.", _executablePath);
        }

        var root = Path.GetFullPath(_workingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var executable = Path.GetFullPath(_executablePath);
        if (!executable.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFileName(executable), _executableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsafe engine executable path.");
        }
        return executable;
    }

    private string ExpandArgument(string argument) => argument
        .Replace("{engine}", _workingDirectory, StringComparison.OrdinalIgnoreCase)
        .Replace("{lua}", Path.Combine(_workingDirectory, "lua"), StringComparison.OrdinalIgnoreCase)
        .Replace("{lists}", AppPaths.ListsDirectory, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> BuildArguments(Strategy strategy)
    {
        var arguments = new List<string>();
        if (_engineType == EngineType.NextGen)
        {
            arguments.AddRange((strategy.LuaFiles ?? []).Select(file => $"--lua-init=@{Path.Combine(_workingDirectory, file)}"));
        }
        arguments.AddRange(StrategyRuntimeTransformer.Transform(strategy.Arguments, _runtimeOptions));
        return arguments.Select(ExpandArgument).ToArray();
    }

    private void EnsureCompatible(Strategy strategy)
    {
        if (strategy.EngineType != _engineType)
            throw new InvalidOperationException($"Strategy {strategy.Id} requires {strategy.EngineType}, not {_engineType}.");
    }

    private void ValidateReferencedFiles(IEnumerable<string> arguments, Strategy strategy)
    {
        var runtimeRoots = new[]
        {
            Path.GetFullPath(_workingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            Path.GetFullPath(AppPaths.ListsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
        };
        foreach (var relative in (strategy.LuaFiles ?? []).Concat(strategy.RequiredFiles ?? []))
        {
            var required = Path.GetFullPath(Path.Combine(_workingDirectory, relative));
            if (!required.StartsWith(runtimeRoots[0], StringComparison.OrdinalIgnoreCase) || !File.Exists(required))
                throw new FileNotFoundException($"Required strategy resource is missing: {relative}", required);
        }
        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf('=');
            if (separator < 0) continue;
            var value = argument[(separator + 1)..];
            var fullPath = Path.GetFullPath(value);
            if (runtimeRoots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) && !File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Required strategy resource is missing: {Path.GetFileName(fullPath)}", fullPath);
            }
        }
    }

    private static bool IsStartupReadyMessage(string? message) => message is not null &&
        (message.Contains("capture is started", StringComparison.OrdinalIgnoreCase)
            || message.Contains("windivert initialized", StringComparison.OrdinalIgnoreCase));

    private void HandleProcessExit(object? sender, EventArgs eventArgs)
    {
        if (_stopping)
        {
            return;
        }
        _crashes.Enqueue(DateTimeOffset.Now);
        _lastError = $"{_executableName} exited unexpectedly with code {_process?.ExitCode}.";
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
        _ = level == "ERROR" ? _logger.ErrorAsync($"{_executableName}: {message}") : _logger.InfoAsync($"{_executableName}: {message}");
    }

    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(ReachabilitySnapshot.Unknown).ConfigureAwait(false);
        _job.Dispose();
        _gate.Dispose();
    }
}

