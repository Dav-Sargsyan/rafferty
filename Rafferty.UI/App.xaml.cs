using System.Threading;
using System.Windows;
using System.IO;
using Rafferty.Shared;

namespace Rafferty.UI;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private RaffertyController? _controller;
    private SettingsService? _settingsService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Local\\Rafferty.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Localization.Apply(Localization.DefaultLanguage);
        try
        {
            _controller = new RaffertyController();
            await _controller.InitializeAsync();
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
                        status.ProcessId,
                        status.CommandLine,
                        workingDirectory = _controller.EngineWorkingDirectory,
                        runtimeDirectory = _controller.RuntimeDirectory,
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
            Localization.Apply(settings.Language);
            var window = new MainWindow(_controller, _settingsService, settings);
            MainWindow = window;
            var renderIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--render-ui", StringComparison.OrdinalIgnoreCase));
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
