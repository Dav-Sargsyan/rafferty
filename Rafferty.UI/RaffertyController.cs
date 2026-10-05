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
    private BypassEngineManager? _engine;
    private OptimizationEngine? _optimizer;
    private ReachabilitySnapshot _reachability = ReachabilitySnapshot.Unknown;
    private OptimizationResult? _lastOptimization;
    private int _recoveryStage;
    private int _recovering;
    private int _optimizerRunning;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _sessionStartedAt = DateTimeOffset.UtcNow;

    public EngineSnapshot Status => Engine.Snapshot(_reachability);
    public string RuntimeDirectory => AppPaths.RuntimeRoot;
    public string EngineWorkingDirectory => Engine.WorkingDirectory;
    public OptimizationResult? LastOptimization => _lastOptimization;

    public Task LogUpdateAsync(string message, CancellationToken token = default) =>
        _logger?.InfoAsync($"Updater: {message}", token) ?? Task.CompletedTask;

    public Task LogUpdateErrorAsync(string message, CancellationToken token = default) =>
        _logger?.ErrorAsync($"Updater: {message}", token) ?? Task.CompletedTask;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await RuntimeExtractor.EnsureAsync(token).ConfigureAwait(false);
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        _logger = new RotatingFileLogger(AppPaths.LogsDirectory, "rafferty");
        _strategies = new StrategyStore(AppPaths.StrategiesFile, AppPaths.CustomStrategiesFile, AppPaths.NextGenStrategiesFile);
        _tester = new ConnectivityTester();
        _engine = new BypassEngineManager(_strategies, _logger);
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
        bool recheckOnStartup = false,
        EngineType preferredEngine = EngineType.Auto,
        IReadOnlyCollection<string>? enabledTargetIds = null)
    {
        checkVoice &= checkDiscord;
        var enabledTargets = enabledTargetIds ?? BuildEnabledTargets(checkYouTube, checkDiscord, checkVoice);
        var state = await _stateStore.LoadAsync(token).ConfigureAwait(false);
        var networkFingerprint = NetworkIdentity.GetCurrent();
        var sameNetwork = string.Equals(state?.LastNetworkId, networkFingerprint, StringComparison.Ordinal);
        var cachedStrategyId = sameNetwork
            ? TryGetCachedStrategy(state?.ServiceStrategies, enabledTargets, networkFingerprint)
            : null;
        var savedStrategyId = preferredEngine switch
        {
            EngineType.Classic => cachedStrategyId ?? state?.LastKnownGoodClassicStrategy ?? state?.LastSuccessfulStrategy ?? state?.ActiveStrategyId,
            EngineType.NextGen => cachedStrategyId ?? state?.LastKnownGoodNextGenStrategy ?? state?.LastSuccessfulStrategy ?? state?.ActiveStrategyId,
            _ => cachedStrategyId ?? state?.LastSuccessfulStrategy ?? state?.ActiveStrategyId
        };
        if (!sameNetwork) savedStrategyId = null;
        var savedEngine = state?.LastSuccessfulEngine is not null and not EngineType.Auto
            ? state.LastSuccessfulEngine
            : state?.ActiveEngine ?? EngineType.Auto;
        Strategy? savedStrategy = null;
        if (!string.IsNullOrWhiteSpace(savedStrategyId))
        {
            try
            {
                savedStrategy = await _strategies!.GetAsync(savedStrategyId, token).ConfigureAwait(false);
                if (preferredEngine != EngineType.Auto && savedStrategy.EngineType != preferredEngine) savedStrategyId = null;
            }
            catch (KeyNotFoundException)
            {
                state = new RuntimeState(null, [], null, NetworkIdentity.GetCurrent(), StrategyHistory: state?.StrategyHistory);
                savedStrategyId = null;
                await _stateStore.SaveAsync(state, token).ConfigureAwait(false);
            }
        }
        if (!string.IsNullOrWhiteSpace(savedStrategyId))
        {
            progress?.Report("Checking saved configuration…");
            try
            {
                await Engine.StartAsync(savedStrategyId, _reachability, token).ConfigureAwait(false);
                var quick = recheckOnStartup
                    ? await Tester.RunDiagnosticsAsync(token).ConfigureAwait(false)
                    : await Tester.RunQuickHealthCheckAsync(enabledTargets, token).ConfigureAwait(false);
                _reachability = ConnectivityTester.Summarize(quick);
                Engine.SetConnectivityVerified(SelectedTargetsWorking(quick, enabledTargets));
                if (SelectedTargetsWorking(quick, enabledTargets))
                {
                    state = (state ?? new RuntimeState(null, [], null)) with
                    {
                        ActiveStrategyId = savedStrategyId,
                        ActiveEngine = Status.EngineType,
                        LastSuccessfulStrategy = savedStrategyId,
                        LastSuccessfulEngine = Status.EngineType,
                        LastSuccessfulTest = DateTimeOffset.Now,
                        LastNetworkId = networkFingerprint,
                        StrategyHistory = MergeHistory(state?.StrategyHistory, [new(savedStrategyId, true, DateTimeOffset.Now)]),
                        LastKnownGoodClassicStrategy = Status.EngineType == EngineType.Classic ? savedStrategyId : state?.LastKnownGoodClassicStrategy,
                        LastKnownGoodNextGenStrategy = Status.EngineType == EngineType.NextGen ? savedStrategyId : state?.LastKnownGoodNextGenStrategy,
                        ServiceStrategies = CacheValidatedServices(state?.ServiceStrategies, savedStrategy!, enabledTargets, networkFingerprint)
                    };
                    await _stateStore.SaveAsync(state, token).ConfigureAwait(false);
                    _recoveryStage = 0;
                    return Status;
                }
                await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
            }
            state = (state ?? new RuntimeState(null, [], null)) with
            {
                StrategyHistory = MergeHistory(state?.StrategyHistory, [new(savedStrategyId, false, DateTimeOffset.Now)])
            };
            await _stateStore.SaveAsync(state, token).ConfigureAwait(false);
            if (!autoFindOnFailure)
            {
                throw new InvalidOperationException("The saved strategy did not pass the health check. Automatic strategy selection is disabled.");
            }
        }

        progress?.Report("Finding the best configuration…");
        var history = state?.StrategyHistory ?? [];
        var recentSuccess = history.Where(item => item.Success).OrderByDescending(item => item.LastTested).Select(item => item.StrategyId);
        var priorityIds = (state?.BackupStrategyIds ?? []).Concat(recentSuccess).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var recentFailures = history.Where(item => !item.Success && item.LastTested > DateTimeOffset.Now.AddDays(-7)).Select(item => item.StrategyId).ToArray();
        if (Interlocked.Exchange(ref _optimizerRunning, 1) != 0)
            throw new InvalidOperationException("Strategy optimization is already running.");
        OptimizationResult result;
        try
        {
            result = await Optimizer.OptimizeAsync(progress, token, checkYouTube, checkDiscord, checkVoice, preferredEngine,
                firstAutoEngine: savedEngine,
                priorityStrategyIds: priorityIds,
                deprioritizedStrategyIds: recentFailures,
                enabledTargetIds: enabledTargets).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _optimizerRunning, 0);
        }
        _lastOptimization = result;
        if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedStrategyId))
        {
            throw new InvalidOperationException(result.Message);
        }

        var selectedScore = result.Scores.Last(score => string.Equals(score.StrategyId, result.SelectedStrategyId, StringComparison.OrdinalIgnoreCase));
        _reachability = ReachabilityFromScore(selectedScore);
        Engine.SetConnectivityVerified(result.FullyWorking);
        var backups = result.Scores.OrderByDescending(score => score.Score)
            .Where(score => !string.Equals(score.StrategyId, result.SelectedStrategyId, StringComparison.OrdinalIgnoreCase))
            .Take(3).Select(score => score.StrategyId).ToArray();
        var selectedStrategy = await _strategies!.GetAsync(result.SelectedStrategyId, token).ConfigureAwait(false);
        await _stateStore.SaveAsync(new(result.SelectedStrategyId, backups, DateTimeOffset.Now, networkFingerprint, Status.EngineType,
            result.SelectedStrategyId, Status.EngineType, MergeHistory(history, HistoryFromScores(result.Scores)),
            Status.EngineType == EngineType.Classic ? result.SelectedStrategyId : state?.LastKnownGoodClassicStrategy,
            Status.EngineType == EngineType.NextGen ? result.SelectedStrategyId : state?.LastKnownGoodNextGenStrategy,
            CacheValidatedServices(state?.ServiceStrategies, selectedStrategy, enabledTargets, networkFingerprint),
            Status.CommandLine), token).ConfigureAwait(false);
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
        IReadOnlyCollection<string>? enabledTargetIds = null,
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
            var enabledTargets = enabledTargetIds ?? BuildEnabledTargets(checkYouTube, checkDiscord, checkVoice);
            var quick = await Tester.RunQuickHealthCheckAsync(enabledTargets, token).ConfigureAwait(false);
            _reachability = ConnectivityTester.Summarize(quick);
            Engine.SetConnectivityVerified(SelectedTargetsWorking(quick, enabledTargets));
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

    public async Task<GoldenComparisonResult> CompareWithGoldenAsync(string strategyId, CancellationToken token = default)
    {
        var strategy = await _strategies!.GetAsync(strategyId, token).ConfigureAwait(false);
        if (strategy.EngineType != EngineType.Classic)
            return new(false, ["Golden comparison is available only for Classic strategies."]);
        return await GoldenStrategyComparer.CompareAsync(strategy, AppPaths.GoldenClassicStrategiesFile, token).ConfigureAwait(false);
    }

    public async Task<EngineSnapshot> ApplyStrategyAsync(
        string strategyId,
        CancellationToken token = default,
        bool checkYouTube = true,
        bool checkDiscord = true,
        bool checkVoice = true,
        IReadOnlyCollection<string>? enabledTargetIds = null)
    {
        checkVoice &= checkDiscord;
        var enabledTargets = enabledTargetIds ?? BuildEnabledTargets(checkYouTube, checkDiscord, checkVoice);
        var strategy = await _strategies!.GetAsync(strategyId, token).ConfigureAwait(false);
        await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
        try
        {
            await Engine.StartAsync(strategy.Id, ReachabilitySnapshot.Unknown, token).ConfigureAwait(false);
            var diagnostics = await Tester.RunQuickHealthCheckAsync(enabledTargets, token).ConfigureAwait(false);
            _reachability = ConnectivityTester.Summarize(diagnostics);
            var verified = SelectedTargetsWorking(diagnostics, enabledTargets);
            Engine.SetConnectivityVerified(verified);
            var previous = await _stateStore.LoadAsync(token).ConfigureAwait(false);
            await _stateStore.SaveAsync(new(strategy.Id, [], DateTimeOffset.Now, NetworkIdentity.GetCurrent(), strategy.EngineType,
                verified ? strategy.Id : previous?.LastSuccessfulStrategy,
                verified ? strategy.EngineType : previous?.LastSuccessfulEngine ?? EngineType.Auto,
                MergeHistory(previous?.StrategyHistory, [new(strategy.Id, verified, DateTimeOffset.Now)]),
                verified && strategy.EngineType == EngineType.Classic ? strategy.Id : previous?.LastKnownGoodClassicStrategy,
                verified && strategy.EngineType == EngineType.NextGen ? strategy.Id : previous?.LastKnownGoodNextGenStrategy,
                verified ? CacheValidatedServices(previous?.ServiceStrategies, strategy, enabledTargets, NetworkIdentity.GetCurrent()) : previous?.ServiceStrategies,
                verified ? Status.CommandLine : previous?.LastKnownGoodCommand), token).ConfigureAwait(false);
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
        bool checkVoice = true,
        EngineType preferredEngine = EngineType.Auto,
        bool deepSearch = true,
        IReadOnlyCollection<string>? enabledTargetIds = null)
    {
        checkVoice &= checkDiscord;
        var enabledTargets = enabledTargetIds ?? BuildEnabledTargets(checkYouTube, checkDiscord, checkVoice);
        await Engine.StopAsync(_reachability, token).ConfigureAwait(false);
        var previous = await _stateStore.LoadAsync(token).ConfigureAwait(false);
        await _stateStore.SaveAsync((previous ?? new RuntimeState(null, [], null)) with { ActiveStrategyId = null, BackupStrategyIds = [] }, token).ConfigureAwait(false);
        if (deepSearch)
        {
            if (Interlocked.Exchange(ref _optimizerRunning, 1) != 0)
                throw new InvalidOperationException("Strategy optimization is already running.");
            OptimizationResult result;
            try
            {
                result = await Optimizer.OptimizeAsync(progress, token, checkYouTube, checkDiscord, checkVoice, preferredEngine,
                    deepSearch: true, enabledTargetIds: enabledTargets).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _optimizerRunning, 0);
            }
            _lastOptimization = result;
            if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedStrategyId)) throw new InvalidOperationException(result.Message);
            var strategy = await _strategies!.GetAsync(result.SelectedStrategyId, token).ConfigureAwait(false);
            var selectedScore = result.Scores.Last(score => string.Equals(score.StrategyId, strategy.Id, StringComparison.OrdinalIgnoreCase));
            _reachability = ReachabilityFromScore(selectedScore);
            await _stateStore.SaveAsync(new(strategy.Id, [], DateTimeOffset.Now, NetworkIdentity.GetCurrent(), strategy.EngineType,
                strategy.Id, strategy.EngineType, MergeHistory(previous?.StrategyHistory, HistoryFromScores(result.Scores)),
                strategy.EngineType == EngineType.Classic ? strategy.Id : previous?.LastKnownGoodClassicStrategy,
                strategy.EngineType == EngineType.NextGen ? strategy.Id : previous?.LastKnownGoodNextGenStrategy,
                CacheValidatedServices(previous?.ServiceStrategies, strategy, enabledTargets, NetworkIdentity.GetCurrent()),
                Status.CommandLine), token).ConfigureAwait(false);
            return Status;
        }
        return await EnableAsync(progress, token, checkYouTube, checkDiscord, checkVoice, autoFindOnFailure: true,
            preferredEngine: preferredEngine, enabledTargetIds: enabledTargets).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunDiagnosticsAsync(CancellationToken token = default)
    {
        var enabled = Engine.RuntimeOptions.EnabledServiceIds is { Count: > 0 } selected
            ? selected
            : new[] { ServiceTargetCatalog.YouTube, ServiceTargetCatalog.Discord, ServiceTargetCatalog.Voice };
        return await RunDiagnosticsAsync(enabled, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunDiagnosticsAsync(
        IReadOnlyCollection<string> enabledTargets,
        CancellationToken token = default)
    {
        var results = await Tester.RunDiagnosticsAsync(enabledTargets, token).ConfigureAwait(false);
        _reachability = ConnectivityTester.Summarize(results);
        if (Engine.Snapshot(_reachability).EngineRunning)
            Engine.SetConnectivityVerified(SelectedTargetsWorking(results, enabledTargets));
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
        var enabledTargets = Engine.RuntimeOptions.EnabledServiceIds is { Count: > 0 } selected
            ? selected
            : new[] { ServiceTargetCatalog.YouTube, ServiceTargetCatalog.Discord, ServiceTargetCatalog.Voice };
        var diagnostics = await RunDiagnosticsAsync(enabledTargets, token).ConfigureAwait(false);
        var prerequisites = await WindowsNetworkPrerequisites.InspectAsync(token).ConfigureAwait(false);
        var snapshot = Status;
        var persistedState = await _stateStore.LoadAsync(token).ConfigureAwait(false);
        var selectedStrategy = snapshot.StrategyId ?? persistedState?.ActiveStrategyId;
        var selectedDefinition = selectedStrategy is null ? null : await _strategies!.GetAsync(selectedStrategy, token).ConfigureAwait(false);
        var activeExecutable = snapshot.EngineType == EngineType.NextGen ? AppPaths.NextGenEngineExecutable : AppPaths.ClassicEngineExecutable;
        var bundledVersion = snapshot.EngineType == EngineType.NextGen ? AppPaths.NextGenEngineVersion : AppPaths.ClassicEngineVersion;
        var version = File.Exists(activeExecutable)
            ? FileVersionInfo.GetVersionInfo(activeExecutable).FileVersion
            : null;
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "0.0.0.0", StringComparison.Ordinal))
            version = bundledVersion;
        var engineHash = File.Exists(activeExecutable)
            ? await HashFileAsync(activeExecutable, token).ConfigureAwait(false)
            : null;
        var lists = new List<object>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.ListsDirectory, "*", SearchOption.AllDirectories).OrderBy(Path.GetFileName))
        {
            var content = await File.ReadAllLinesAsync(file, token).ConfigureAwait(false);
            lists.Add(new
            {
                name = Path.GetRelativePath(AppPaths.ListsDirectory, file),
                size = new FileInfo(file).Length,
                sha256 = await HashFileAsync(file, token).ConfigureAwait(false),
                domainCount = content.Count(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            });
        }
        var logPath = Path.Combine(AppPaths.LogsDirectory, "rafferty.log");
        var errors = File.Exists(logPath)
            ? File.ReadLines(logPath).Where(line => line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)).TakeLast(100)
                .Select(Sanitize).ToArray()
            : [];
        var currentErrors = File.Exists(logPath)
            ? File.ReadLines(logPath).Where(line => line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) && IsCurrentSessionLogLine(line)).TakeLast(100)
                .Select(Sanitize).ToArray()
            : [];
        IReadOnlyList<object> requiredFiles = selectedDefinition is null ? [] : VerifyStrategyFiles(selectedDefinition);
        var report = new
        {
            product = "Rafferty",
            version = AppVersion.Display,
            sessionId = _sessionId,
            sessionStartedAt = _sessionStartedAt,
            generatedAtUtc = DateTimeOffset.UtcNow,
            windows = Environment.OSVersion.VersionString,
            engineVersion = version,
            engineBinarySha256 = engineHash,
            engineType = snapshot.EngineType,
            runtimePackageVersion = AppPaths.RuntimePackageVersion,
            classicRuntimeVersion = AppPaths.ClassicEngineVersion,
            nextGenRuntimeVersion = AppPaths.NextGenEngineVersion,
            strategyPackVersion = AppPaths.StrategyPackVersion,
            referenceRevision = "249a70424aae2676f99c5363e21073ed89873eda",
            administrator = snapshot.IsAdministrator,
            baseFilteringEngineRunning = prerequisites.BaseFilteringEngineRunning,
            tcpTimestamps = prerequisites.TcpTimestamps,
            potentiallyConflictingNetworkServices = prerequisites.PotentiallyConflictingServices,
            driverActive = snapshot.DriverActive,
            strategyApplied = snapshot.StrategyApplied,
            connectivityVerified = snapshot.ConnectivityVerified,
            engineRunning = snapshot.EngineRunning,
            optimizerRunning = Volatile.Read(ref _optimizerRunning) != 0,
            recoveryRunning = Volatile.Read(ref _recovering) != 0,
            selectedStrategy,
            lastKnownGoodClassicStrategy = persistedState?.LastKnownGoodClassicStrategy,
            lastKnownGoodNextGenStrategy = persistedState?.LastKnownGoodNextGenStrategy,
            enabledServices = Engine.RuntimeOptions.EnabledServiceIds ?? [],
            processId = snapshot.ProcessId,
            actualCommandLine = selectedStrategy is null ? null : await Engine.GetCommandLineAsync(selectedStrategy, false, token).ConfigureAwait(false),
            sanitizedCommandLine = selectedStrategy is null ? null : await Engine.GetCommandLineAsync(selectedStrategy, true, token).ConfigureAwait(false),
            requiredFileVerification = requiredFiles,
            connectivity = diagnostics,
            listFiles = lists,
            currentSessionErrors = currentErrors,
            historicalEngineErrors = errors
        };
        var directory = outputPath is null ? AppContext.BaseDirectory : Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var path = outputPath is null ? Path.Combine(directory, $"Rafferty-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json") : Path.GetFullPath(outputPath);
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonDefaults.Options), token).ConfigureAwait(false);
            return path;
        }
        catch (Exception exception) when (outputPath is null && exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Не удалось сохранить диагностику рядом с Rafferty.exe: {exception.Message}", exception);
        }
    }

    private static IReadOnlyList<object> VerifyStrategyFiles(Strategy strategy)
    {
        var engineRoot = strategy.EngineType == EngineType.NextGen ? AppPaths.NextGenEngineDirectory : AppPaths.ClassicEngineDirectory;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in (strategy.LuaFiles ?? []).Concat(strategy.RequiredFiles ?? []))
            files.Add(Path.Combine(engineRoot, relative));
        foreach (var argument in strategy.Arguments)
        {
            var separator = argument.IndexOf('=');
            if (separator < 0) continue;
            var value = argument[(separator + 1)..];
            if (value.StartsWith("{engine}\\", StringComparison.OrdinalIgnoreCase)) files.Add(Path.Combine(engineRoot, value[9..]));
            if (value.StartsWith("{lists}\\", StringComparison.OrdinalIgnoreCase)) files.Add(Path.Combine(AppPaths.ListsDirectory, value[8..]));
        }
        return files.Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => (object)new { file = Path.GetFileName(path), path = Sanitize(path), exists = File.Exists(path) })
            .ToArray();
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

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private bool IsCurrentSessionLogLine(string line)
    {
        var separator = line.IndexOf(' ');
        return separator > 0
            && DateTimeOffset.TryParse(line[..separator], out var timestamp)
            && timestamp >= _sessionStartedAt;
    }

    private async Task RecoverAfterCrashAsync()
    {
        // An optimizer owns deliberate stop/start transitions. Recovery must not
        // race it and spawn another WinDivert filter.
        if (Volatile.Read(ref _optimizerRunning) != 0) return;
        if (Interlocked.Exchange(ref _recovering, 1) != 0) return;
        try
        {
            await Task.Delay(1000).ConfigureAwait(false);
            if (Volatile.Read(ref _optimizerRunning) != 0) return;
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

    private static IReadOnlyCollection<string> BuildEnabledTargets(bool checkYouTube, bool checkDiscord, bool checkVoice)
    {
        var targets = new List<string>();
        if (checkYouTube) targets.Add(ServiceTargetCatalog.YouTube);
        if (checkDiscord) targets.Add(ServiceTargetCatalog.Discord);
        if (checkVoice && checkDiscord) targets.Add(ServiceTargetCatalog.Voice);
        return targets;
    }

    private static bool SelectedTargetsWorking(IReadOnlyList<DiagnosticResult> results, IReadOnlyCollection<string> enabledTargets)
    {
        var states = ServiceTargetCatalog.SummarizeTargets(results, enabledTargets);
        return enabledTargets.All(id => states.TryGetValue(id, out var state) && state == ServiceReachability.Working);
    }

    private static ReachabilitySnapshot ReachabilityFromScore(StrategyScore score) => new(
        ServiceReachability.NotTested, score.YouTube, score.Discord, score.Voice, DateTimeOffset.Now, score.TargetResults);

    private static IReadOnlyList<StrategyHistoryEntry> HistoryFromScores(IEnumerable<StrategyScore> scores) =>
        scores.Select(score => new StrategyHistoryEntry(score.StrategyId,
            score.EngineStarted && score.DriverActive && score.Errors == 0
                && (score.TargetResults?.Values.All(state => state is ServiceReachability.Working or ServiceReachability.NotTested)
                    ?? ((score.YouTube is ServiceReachability.Working or ServiceReachability.NotTested)
                        && (score.Discord is ServiceReachability.Working or ServiceReachability.NotTested)
                        && (score.Voice is ServiceReachability.Working or ServiceReachability.NotTested))),
            DateTimeOffset.Now)).ToArray();

    private static IReadOnlyList<StrategyHistoryEntry> MergeHistory(
        IReadOnlyList<StrategyHistoryEntry>? existing,
        IEnumerable<StrategyHistoryEntry> updates)
    {
        var merged = (existing ?? []).ToDictionary(item => item.StrategyId, StringComparer.OrdinalIgnoreCase);
        foreach (var update in updates) merged[update.StrategyId] = update;
        return merged.Values.OrderByDescending(item => item.LastTested).Take(64).ToArray();
    }

    private static string? TryGetCachedStrategy(
        IReadOnlyList<ServiceStrategyResult>? results,
        IReadOnlyCollection<string> enabledTargets,
        string networkFingerprint)
    {
        if (enabledTargets.Count == 0) return null;
        var winners = ServiceStrategyCache.ForNetwork(results, networkFingerprint);
        var selected = enabledTargets
            .Select(id => winners.TryGetValue(id, out var result) ? result : null)
            .ToArray();
        if (selected.Any(result => result is null)) return null;
        var families = selected.Select(result => result!.StrategyFamily).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // A divergent per-service cache is intentionally not flattened back
        // into a global strategy. It will be used once the composite launcher
        // is selected, rather than silently discarding a service winner.
        return families.Length == 1 ? families[0] : null;
    }

    private static IReadOnlyList<ServiceStrategyResult> CacheValidatedServices(
        IReadOnlyList<ServiceStrategyResult>? existing,
        Strategy strategy,
        IReadOnlyCollection<string> enabledTargets,
        string networkFingerprint)
    {
        var results = existing ?? [];
        foreach (var targetId in enabledTargets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = new ServiceStrategyResult(targetId, strategy.EngineType, strategy.Id, strategy.Arguments,
                string.Join(',', strategy.Protocols), DateTimeOffset.UtcNow, networkFingerprint, 100);
            results = ServiceStrategyCache.Upsert(results, result);
        }
        return results;
    }

    private BypassEngineManager Engine => _engine ?? throw new InvalidOperationException("Rafferty is not initialized.");
    private ConnectivityTester Tester => _tester ?? throw new InvalidOperationException("Rafferty is not initialized.");
    private OptimizationEngine Optimizer => _optimizer ?? throw new InvalidOperationException("Rafferty is not initialized.");

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync().ConfigureAwait(false);
        _tester?.Dispose();
    }
}
