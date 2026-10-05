using System.Threading;
using System.Windows;
using System.IO;
using Rafferty.Core;
using Rafferty.Shared;

namespace Rafferty.UI;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private RaffertyController? _controller;
    private SettingsService? _settingsService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var applyUpdateIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--apply-update", StringComparison.OrdinalIgnoreCase));
        if (applyUpdateIndex >= 0 && applyUpdateIndex + 2 < e.Args.Length)
        {
            try
            {
                var target = e.Args[applyUpdateIndex + 1];
                var previousPid = int.Parse(e.Args[applyUpdateIndex + 2], System.Globalization.CultureInfo.InvariantCulture);
                await SelfUpdateInstaller.ApplyAsync(target, previousPid);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                System.Windows.MessageBox.Show(exception.Message, "Rafferty Update", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(10);
            }
            return;
        }

        _singleInstance = new Mutex(true, "Local\\Rafferty.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        Localization.Apply(Localization.DefaultLanguage);
        try
        {
            _controller = new RaffertyController();
            await _controller.InitializeAsync();
            string? postUpdateReadyMarker = null;
            var postUpdateReadyIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--post-update-ready", StringComparison.OrdinalIgnoreCase));
            if (postUpdateReadyIndex >= 0 && postUpdateReadyIndex + 1 < e.Args.Length)
            {
                postUpdateReadyMarker = Path.GetFullPath(e.Args[postUpdateReadyIndex + 1]);
            }
            var exportCommandsIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--export-command-lines", StringComparison.OrdinalIgnoreCase));
            if (exportCommandsIndex >= 0 && exportCommandsIndex + 1 < e.Args.Length)
            {
                await _controller.ExportCommandLinesAsync(e.Args[exportCommandsIndex + 1]);
                Shutdown();
                return;
            }
            var exportDiagnosticsIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--export-diagnostics", StringComparison.OrdinalIgnoreCase));
            if (exportDiagnosticsIndex >= 0 && exportDiagnosticsIndex + 1 < e.Args.Length)
            {
                await _controller.ExportDiagnosticsAsync(e.Args[exportDiagnosticsIndex + 1]);
                Shutdown();
                return;
            }
            var validateStrategiesIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--validate-strategies", StringComparison.OrdinalIgnoreCase));
            if (validateStrategiesIndex >= 0 && validateStrategiesIndex + 1 < e.Args.Length)
            {
                var passed = await _controller.ValidateAllStrategiesAsync(e.Args[validateStrategiesIndex + 1]);
                Shutdown(passed ? 0 : 4);
                return;
            }
            var parityTestIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--parity-test", StringComparison.OrdinalIgnoreCase));
            if (parityTestIndex >= 0 && parityTestIndex + 1 < e.Args.Length)
            {
                var report = await _controller.RunReferenceParityTestAsync();
                await File.WriteAllTextAsync(e.Args[parityTestIndex + 1], report);
                var status = _controller.Status;
                Shutdown(status.EngineRunning && status.DriverActive && status.StrategyApplied && status.ConnectivityVerified ? 0 : 5);
                return;
            }
            var runtimeStrategyIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--runtime-strategy", StringComparison.OrdinalIgnoreCase));
            if (runtimeStrategyIndex >= 0 && runtimeStrategyIndex + 4 < e.Args.Length)
            {
                var strategyId = e.Args[runtimeStrategyIndex + 1];
                var mode = Enum.Parse<IpSetMode>(e.Args[runtimeStrategyIndex + 2], true);
                var gameFilter = bool.Parse(e.Args[runtimeStrategyIndex + 3]);
                var reportPath = Path.GetFullPath(e.Args[runtimeStrategyIndex + 4]);
                EngineSnapshot? status = null;
                try
                {
                    await _controller.ConfigureRuntimeAsync(new EngineRuntimeOptions(mode, gameFilter), false);
                    status = await _controller.ApplyStrategyAsync(strategyId);
                    var report = new { strategyId, mode, gameFilter, status };
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                    await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, JsonDefaults.Options));
                }
                finally
                {
                    await _controller.DisableAsync();
                }
                Shutdown(status is { EngineRunning: true, DriverActive: true, StrategyApplied: true } ? 0 : 11);
                return;
            }
            var referenceCompatibleIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--reference-compatible", StringComparison.OrdinalIgnoreCase));
            if (referenceCompatibleIndex >= 0 && referenceCompatibleIndex + 2 < e.Args.Length)
            {
                var strategyId = e.Args[referenceCompatibleIndex + 1];
                var reportPath = Path.GetFullPath(e.Args[referenceCompatibleIndex + 2]);
                EngineSnapshot? status = null;
                try
                {
                    await _controller.ConfigureRuntimeAsync(new EngineRuntimeOptions(
                        IpSetMode.Loaded, false, ReferenceCompatible: true), false);
                    status = await _controller.ApplyStrategyAsync(strategyId, checkDiscord: false, checkVoice: false);
                    var golden = await _controller.CompareWithGoldenAsync(strategyId);
                    var report = new { strategyId, referenceCompatible = true, golden, status };
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                    await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, JsonDefaults.Options));
                }
                finally
                {
                    await _controller.DisableAsync();
                }
                Shutdown(status is { EngineRunning: true, DriverActive: true, StrategyApplied: true } ? 0 : 14);
                return;
            }
            var manualStrategyIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--manual-strategy", StringComparison.OrdinalIgnoreCase));
            if (manualStrategyIndex >= 0 && manualStrategyIndex + 2 < e.Args.Length)
            {
                var strategyId = e.Args[manualStrategyIndex + 1];
                var reportPath = Path.GetFullPath(e.Args[manualStrategyIndex + 2]);
                EngineSnapshot? status = null;
                IReadOnlyList<DiagnosticResult> diagnostics = [];
                try
                {
                    status = await _controller.ApplyStrategyAsync(strategyId);
                    diagnostics = await _controller.RunDiagnosticsAsync();
                    var report = new
                    {
                        strategyId,
                        status.EngineRunning,
                        status.DriverActive,
                        status.StrategyApplied,
                        status.EngineType,
                        status.ProcessId,
                        status.CommandLine,
                        workingDirectory = _controller.EngineWorkingDirectory,
                        runtimeDirectory = _controller.RuntimeDirectory,
                        luaRuntimeReady = status.EngineType != EngineType.NextGen ||
                            (File.Exists(Path.Combine(_controller.EngineWorkingDirectory, "lua", "zapret-lib.lua")) &&
                             File.Exists(Path.Combine(_controller.EngineWorkingDirectory, "lua", "zapret-antidpi.lua"))),
                        diagnostics
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                    await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, JsonDefaults.Options));
                }
                finally
                {
                    await _controller.DisableAsync();
                }
                Shutdown(status is { EngineRunning: true, DriverActive: true, StrategyApplied: true } ? 0 : 6);
                return;
            }
            var cachedAutoIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--cached-auto-test", StringComparison.OrdinalIgnoreCase));
            if (cachedAutoIndex >= 0 && cachedAutoIndex + 1 < e.Args.Length)
            {
                var reportPath = Path.GetFullPath(e.Args[cachedAutoIndex + 1]);
                var messages = new List<string>();
                var status = await _controller.EnableAsync(new Progress<string>(messages.Add), checkYouTube: true, checkDiscord: false, checkVoice: false);
                await Task.Delay(100);
                var fullOptimizationTriggered = messages.Any(message => message.StartsWith("Finding", StringComparison.OrdinalIgnoreCase));
                var report = new { status, progress = messages, fullOptimizationTriggered, optimization = _controller.LastOptimization };
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, JsonDefaults.Options));
                await _controller.DisableAsync();
                Shutdown(status.EngineRunning && status.DriverActive && status.StrategyApplied && !fullOptimizationTriggered ? 0 : 12);
                return;
            }
            var autoOptimizeIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--auto-optimize", StringComparison.OrdinalIgnoreCase));
            if (autoOptimizeIndex >= 0 && autoOptimizeIndex + 1 < e.Args.Length)
            {
                var reportPath = Path.GetFullPath(e.Args[autoOptimizeIndex + 1]);
                var status = await _controller.ReoptimizeAsync();
                var report = new { status, optimization = _controller.LastOptimization };
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, JsonDefaults.Options));
                await _controller.DisableAsync();
                Shutdown(status.EngineRunning && status.DriverActive && status.StrategyApplied ? 0 : 7);
                return;
            }
            if (e.Args.Contains("--engine-hold", StringComparer.OrdinalIgnoreCase))
            {
                await _controller.EnableAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return;
            }

            if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                var status = await _controller.EnableAsync();
                var diagnostics = await _controller.RunDiagnosticsAsync();
                var healthy = status.EngineRunning && status.IsAdministrator && status.DriverActive && status.StrategyApplied && status.ConnectivityVerified && diagnostics
                    .Where(result => result.Id is "youtube" or "googlevideo" or "discord-api" or "discord-stun")
                    .All(result => result.State == DiagnosticState.Success);
                await _controller.DisableAsync();
                Shutdown(healthy ? 0 : 2);
                return;
            }

            if (e.Args.Contains("--latency-test", StringComparer.OrdinalIgnoreCase))
            {
                using var tester = new LatencyTester();
                var tests = await Task.WhenAll(tester.TestYouTubeAsync(), tester.TestDiscordAsync(), tester.TestDiscordVoiceAsync());
                Shutdown(tests.All(result => result.Success && result.LatencyMs is not null) ? 0 : 3);
                return;
            }

            _settingsService = new SettingsService();
            var settings = await _settingsService.LoadAsync();
            var renderIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--render-ui", StringComparison.OrdinalIgnoreCase));
            if (renderIndex >= 0) settings = settings with { AutoCheckUpdates = false };
            await _controller.ConfigureRuntimeAsync(new EngineRuntimeOptions(
                settings.IpSetMode,
                settings.GameFilterEnabled,
                settings.GameFilterTcp,
                settings.GameFilterUdp,
                ServiceTargetCatalog.EnabledFromSettings(settings).ToArray()), false,
                settings.CheckYouTube, settings.CheckDiscord, settings.CheckVoice);
            Localization.Apply(settings.Language);
            var window = new MainWindow(_controller, _settingsService, settings);
            MainWindow = window;
            if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
            {
                window.Show();
                var language = renderIndex + 2 < e.Args.Length ? e.Args[renderIndex + 2] : null;
                await window.RenderPagesForTestAsync(e.Args[renderIndex + 1], language);
                window.CloseForTest();
                Shutdown();
                return;
            }
            if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) window.Show();
            if (settings.StartEnabled) await window.StartEnabledAsync();
            if (postUpdateReadyMarker is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(postUpdateReadyMarker)!);
                await File.WriteAllTextAsync(postUpdateReadyMarker, AppVersion.Display);
            }
        }
        catch (Exception exception)
        {
            if (!e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show(Localization.T("InitializationError") + "\n\n" + exception.Message, Localization.T("AppName"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_controller is not null) _controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
