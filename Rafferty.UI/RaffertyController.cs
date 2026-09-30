using Rafferty.Core;
using Rafferty.Shared;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rafferty.UI;

internal sealed class RaffertyController : IAsyncDisposable
{
    private readonly JsonFileStore<RuntimeState> _stateStore = new(AppPaths.RuntimeFile);
    private RotatingFileLogger? _logger;
    private StrategyStore? _strategies;
    private ConnectivityTester? _tester;
    private EngineManager? _engine;
    private OptimizationEngine? _optimizer;
    private ReachabilitySnapshot _reachability = ReachabilitySnapshot.Unknown;
    private OptimizationResult? _lastOptimization;
    private int _recoveryStage;
    private int _recovering;

    public EngineSnapshot Status => Engine.Snapshot(_reachability);
    public string RuntimeDirectory => AppPaths.RuntimeRoot;
    public string EngineWorkingDirectory => Engine.WorkingDirectory;
    public OptimizationResult? LastOptimization => _lastOptimization;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await RuntimeExtractor.EnsureAsync(token).ConfigureAwait(false);
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        _logger = new RotatingFileLogger(AppPaths.LogsDirectory, "rafferty");
        _strategies = new StrategyStore(AppPaths.StrategiesFile, AppPaths.CustomStrategiesFile);
        _tester = new ConnectivityTester();
        _engine = new EngineManager(_strategies, _logger);
        _optimizer = new OptimizationEngine(_strategies, _engine, _tester, _logger);
        _engine.UnexpectedExit += (_, _) => _ = RecoverAfterCrashAsync();
    }

    public async Task<EngineSnapshot> EnableAsync(
        IProgress<string>? progress = null,
        CancellationToken token = default,
        bool checkYouTube = true,
        bool checkDiscord = true,
        bool checkVoice = true,
        bool autoFindOnFailure = true,
        bool recheckOnStartup = false)
    {
        var state = await _stateStore.LoadAsync(token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(state?.ActiveStrategyId))
        {
            try { await _strategies!.GetAsync(state.ActiveStrategyId, token).ConfigureAwait(false); }
            catch (KeyNotFoundException)
            {
                state = new RuntimeState(null, [], null, NetworkIdentity.GetCurrent());
                await _stateStore.SaveAsync(state, token).ConfigureAwait(false);
            }
        }
        if (!string.IsNullOrWhiteSpace(state?.ActiveStrategyId))
        {
            progress?.Report("Checking saved configuration…");
            try
            {
                await Engine.StartAsync(state.ActiveStrategyId, _reachability, token).ConfigureAwait(false);
                var quick = recheckOnStartup
                    ? await Tester.RunDiagnosticsAsync(token).ConfigureAwait(false)
                    : await Tester.RunQuickHealthCheckAsync(checkYouTube, checkDiscord, checkVoice, token).ConfigureAwait(false);
                _reachability = ConnectivityTester.Summarize(quick);
                Engine.SetConnectivityVerified(SelectedServicesWorking(quick, checkYouTube, checkDiscord, checkVoice));
                if (SelectedServicesWorking(quick, checkYouTube, checkDiscord, checkVoice))
                {
                    _recoveryStage = 0;
                    return Status;
                }
                await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
            }
            if (!autoFindOnFailure)
            {
                throw new InvalidOperationException("The saved strategy did not pass the health check. Automatic strategy selection is disabled.");
            }
        }

        progress?.Report("Finding the best configuration…");
        var result = await Optimizer.OptimizeAsync(progress, token, checkYouTube, checkDiscord, checkVoice).ConfigureAwait(false);
        _lastOptimization = result;
        if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedStrategyId))
        {
            throw new InvalidOperationException(result.Message);
        }

        var diagnostics = await Tester.RunDiagnosticsAsync(token).ConfigureAwait(false);
        _reachability = ConnectivityTester.Summarize(diagnostics);
        Engine.SetConnectivityVerified(SelectedServicesWorking(diagnostics, checkYouTube, checkDiscord, checkVoice));
        var backups = result.Scores.OrderByDescending(score => score.Score)
            .Where(score => !string.Equals(score.StrategyId, result.SelectedStrategyId, StringComparison.OrdinalIgnoreCase))
            .Take(3).Select(score => score.StrategyId).ToArray();
        await _stateStore.SaveAsync(new(result.SelectedStrategyId, backups, DateTimeOffset.Now, NetworkIdentity.GetCurrent()), token).ConfigureAwait(false);
        _recoveryStage = 0;
        return Status;
    }

    public async Task<EngineSnapshot> DisableAsync(CancellationToken token = default) =>
        await Engine.StopAsync(_reachability, token).ConfigureAwait(false);

    public async Task<IReadOnlyList<Strategy>> GetStrategiesAsync(CancellationToken token = default) =>
        (await _strategies!.LoadAsync(token).ConfigureAwait(false)).Strategies;

    public async Task<DateTimeOffset> GetStrategyDatabaseUpdatedAtAsync(CancellationToken token = default) =>
        (await _strategies!.LoadAsync(token).ConfigureAwait(false)).UpdatedAt;

    public async Task<EngineSnapshot> ConfigureRuntimeAsync(
        EngineRuntimeOptions options,
        bool restartIfActive,
        bool checkYouTube = true,
        bool checkDiscord = true,
        bool checkVoice = true,
        CancellationToken token = default)
    {
        var activeStrategy = Status.EngineRunning ? Status.StrategyId : null;
        if (restartIfActive && !string.IsNullOrWhiteSpace(activeStrategy))
        {
            await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
        }
        Engine.SetRuntimeOptions(options);
        if (restartIfActive && !string.IsNullOrWhiteSpace(activeStrategy))
        {
            await Engine.StartAsync(activeStrategy, ReachabilitySnapshot.Unknown, token).ConfigureAwait(false);
            var quick = await Tester.RunQuickHealthCheckAsync(checkYouTube, checkDiscord, checkVoice, token).ConfigureAwait(false);
            _reachability = ConnectivityTester.Summarize(quick);
            Engine.SetConnectivityVerified(SelectedServicesWorking(quick, checkYouTube, checkDiscord, checkVoice));
        }
        return Status;
    }

    public async Task<IReadOnlyList<Strategy>> ReloadStrategiesAsync(CancellationToken token = default)
    {
        _strategies!.Invalidate();
        return (await _strategies.LoadAsync(token).ConfigureAwait(false)).Strategies;
    }

    public async Task<Strategy> ImportStrategyAsync(string batPath, CancellationToken token = default)
    {
        if (!File.Exists(batPath)) throw new FileNotFoundException("BAT strategy was not found.", batPath);
        var name = Path.GetFileNameWithoutExtension(batPath);
        var slug = new string(name.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        var strategy = BatStrategyImporter.Import($"custom--{slug}", name, await File.ReadAllTextAsync(batPath, token).ConfigureAwait(false),
            "User-imported BAT strategy. Verify its origin and runtime dependencies before use.");
        await _strategies!.AddOrReplaceAsync(strategy, token).ConfigureAwait(false);
        return strategy;
    }

    public Task<string> GetCommandLineAsync(string strategyId, CancellationToken token = default) =>
        Engine.GetCommandLineAsync(strategyId, false, token);

    public async Task<EngineSnapshot> ApplyStrategyAsync(string strategyId, CancellationToken token = default)
    {
        var strategy = await _strategies!.GetAsync(strategyId, token).ConfigureAwait(false);
        await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
        try
        {
            await Engine.StartAsync(strategy.Id, ReachabilitySnapshot.Unknown, token).ConfigureAwait(false);
            var diagnostics = await Tester.RunDiagnosticsAsync(token).ConfigureAwait(false);
            _reachability = ConnectivityTester.Summarize(diagnostics);
            Engine.SetConnectivityVerified(CriticalChecksPassed(diagnostics));
            await _stateStore.SaveAsync(new(strategy.Id, [], DateTimeOffset.Now, NetworkIdentity.GetCurrent()), token).ConfigureAwait(false);
            return Status;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
            throw new InvalidOperationException($"Could not start {StrategyNames.DisplayName(strategy)}. {exception.Message}", exception);
        }
    }

    public async Task<EngineSnapshot> ReoptimizeAsync(
        IProgress<string>? progress = null,
        CancellationToken token = default,
        bool checkYouTube = true,
        bool checkDiscord = true,
        bool checkVoice = true)
    {
        await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
        await _stateStore.SaveAsync(new(null, [], null), token).ConfigureAwait(false);
        return await EnableAsync(progress, token, checkYouTube, checkDiscord, checkVoice, autoFindOnFailure: true).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunDiagnosticsAsync(CancellationToken token = default)
    {
        var results = await Tester.RunDiagnosticsAsync(token).ConfigureAwait(false);
        _reachability = ConnectivityTester.Summarize(results);
        if (Engine.Snapshot(_reachability).EngineRunning) Engine.SetConnectivityVerified(CriticalChecksPassed(results));
        return results;
    }

    public async Task<string> RunReferenceParityTestAsync(CancellationToken token = default)
    {
        var current = Status;
        var strategyId = current.StrategyId ?? (await _stateStore.LoadAsync(token).ConfigureAwait(false))?.ActiveStrategyId ?? "general";
        if (current.EngineRunning) await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
        var baseline = await Tester.RunDiagnosticsAsync(token).ConfigureAwait(false);
        await Engine.StartAsync(strategyId, ConnectivityTester.Summarize(baseline), token).ConfigureAwait(false);
        var active = await Tester.RunDiagnosticsAsync(token).ConfigureAwait(false);
        _reachability = ConnectivityTester.Summarize(active);
        Engine.SetConnectivityVerified(CriticalChecksPassed(active));

        var lines = new List<string> { $"Strategy: {strategyId}", $"Driver: {(Status.DriverActive ? "RUNNING" : "NOT RUNNING")}", "" };
        foreach (var on in active)
        {
            var off = baseline.FirstOrDefault(result => result.Id == on.Id);
            lines.Add($"{on.Name}: OFF={off?.State} ({off?.Detail}) | ON={on.State} ({on.Detail})");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public async Task<string> ExportDiagnosticsAsync(string? outputPath = null, CancellationToken token = default)
    {
        var diagnostics = await RunDiagnosticsAsync(token).ConfigureAwait(false);
        var snapshot = Status;
        var persistedState = await _stateStore.LoadAsync(token).ConfigureAwait(false);
        var selectedStrategy = snapshot.StrategyId ?? persistedState?.ActiveStrategyId;
        var version = FileVersionInfo.GetVersionInfo(AppPaths.EngineExecutable).FileVersion ?? "unknown";
        var lists = new List<object>();
        foreach (var file in Directory.GetFiles(AppPaths.ListsDirectory).OrderBy(Path.GetFileName))
        {
            await using var stream = File.OpenRead(file);
            lists.Add(new { name = Path.GetFileName(file), size = stream.Length, sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) });
        }
        var logPath = Path.Combine(AppPaths.LogsDirectory, "rafferty.log");
        var errors = File.Exists(logPath)
            ? File.ReadLines(logPath).Where(line => line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)).TakeLast(100)
                .Select(Sanitize).ToArray()
            : [];
        var report = new
        {
            product = "Rafferty",
            version = AppVersion.Display,
            generatedAtUtc = DateTimeOffset.UtcNow,
            windows = Environment.OSVersion.VersionString,
            engineVersion = version,
            referenceRevision = "249a70424aae2676f99c5363e21073ed89873eda",
            administrator = snapshot.IsAdministrator,
            driverActive = snapshot.DriverActive,
            strategyApplied = snapshot.StrategyApplied,
            connectivityVerified = snapshot.ConnectivityVerified,
            selectedStrategy,
            processId = snapshot.ProcessId,
            commandLine = selectedStrategy is null ? null : await Engine.GetCommandLineAsync(selectedStrategy, true, token).ConfigureAwait(false),
            connectivity = diagnostics,
            listFiles = lists,
            engineErrors = errors
        };
        var directory = outputPath is null ? Path.Combine(AppPaths.UserDataRoot, "diagnostics") : Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        var path = outputPath is null ? Path.Combine(directory, $"Rafferty-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json") : Path.GetFullPath(outputPath);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonDefaults.Options), token).ConfigureAwait(false);
        return path;
    }

    public async Task ExportCommandLinesAsync(string path, CancellationToken token = default)
    {
        var database = await _strategies!.LoadAsync(token).ConfigureAwait(false);
        var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in database.Strategies)
            commands[strategy.Id] = await Engine.GetCommandLineAsync(strategy.Id, true, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(commands, JsonDefaults.Options), token).ConfigureAwait(false);
    }

    public async Task<bool> ValidateAllStrategiesAsync(string path, CancellationToken token = default)
    {
        var database = await _strategies!.LoadAsync(token).ConfigureAwait(false);
        var results = new List<object>();
        var allPassed = true;
        foreach (var strategy in database.Strategies)
        {
            try
            {
                var snapshot = await Engine.StartAsync(strategy.Id, ReachabilitySnapshot.Unknown, token).ConfigureAwait(false);
                results.Add(new { strategy = strategy.Id, success = snapshot.EngineRunning && snapshot.DriverActive && snapshot.StrategyApplied, snapshot.ProcessId, commandLine = await Engine.GetCommandLineAsync(strategy.Id, true, token).ConfigureAwait(false), error = (string?)null });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                allPassed = false;
                results.Add(new { strategy = strategy.Id, success = false, ProcessId = (int?)null, commandLine = await Engine.GetCommandLineAsync(strategy.Id, true, token).ConfigureAwait(false), error = exception.Message });
            }
            finally
            {
                await Engine.StopAsync(ReachabilitySnapshot.Unknown, token).ConfigureAwait(false);
            }
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(results, JsonDefaults.Options), token).ConfigureAwait(false);
        return allPassed;
    }

    private static string Sanitize(string value) => value
        .Replace(AppPaths.UserDataRoot, "{localAppData}\\Rafferty", StringComparison.OrdinalIgnoreCase)
        .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "{userProfile}", StringComparison.OrdinalIgnoreCase);

    private async Task RecoverAfterCrashAsync()
    {
        if (Interlocked.Exchange(ref _recovering, 1) != 0) return;
        try
        {
            await Task.Delay(1000).ConfigureAwait(false);
            var state = await _stateStore.LoadAsync().ConfigureAwait(false);
            if (state is null) return;

            string? strategy = _recoveryStage switch
            {
                0 => state.ActiveStrategyId,
                1 when state.BackupStrategyIds.Count > 0 => state.BackupStrategyIds[0],
                _ => null
            };
            _recoveryStage++;
            if (!string.IsNullOrWhiteSpace(strategy))
            {
                await Engine.StartAsync(strategy, _reachability).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (_logger is not null) await _logger.ErrorAsync($"Automatic recovery failed: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    private static bool CriticalChecksPassed(IReadOnlyList<DiagnosticResult> results)
    {
        var required = new[] { "youtube", "googlevideo", "discord-api", "discord-cdn", "discord-gateway", "discord-stun" };
        return required.All(id => results.Any(result => result.Id == id && result.State == DiagnosticState.Success));
    }

    private static bool SelectedServicesWorking(IReadOnlyList<DiagnosticResult> results, bool checkYouTube, bool checkDiscord, bool checkVoice)
    {
        var summary = ConnectivityTester.Summarize(results);
        return (!checkYouTube || summary.YouTube == ServiceReachability.Working)
            && (!checkDiscord || summary.Discord == ServiceReachability.Working)
            && (!checkVoice || summary.DiscordVoice == ServiceReachability.Working);
    }

    private EngineManager Engine => _engine ?? throw new InvalidOperationException("Rafferty is not initialized.");
    private ConnectivityTester Tester => _tester ?? throw new InvalidOperationException("Rafferty is not initialized.");
    private OptimizationEngine Optimizer => _optimizer ?? throw new InvalidOperationException("Rafferty is not initialized.");

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync().ConfigureAwait(false);
        _tester?.Dispose();
    }
}
