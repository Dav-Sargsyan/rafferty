using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Rafferty.Core;
using Rafferty.Shared;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;

namespace Rafferty.UI;

public partial class MainWindow : Window
{
    private enum AppPage { Dashboard, Settings, Advanced }

    private static readonly WpfBrush IdleBrush = new SolidColorBrush(WpfColor.FromRgb(75, 80, 96));
    private static readonly WpfBrush SuccessBrush = new SolidColorBrush(WpfColor.FromRgb(99, 230, 177));
    private static readonly WpfBrush WarningBrush = new SolidColorBrush(WpfColor.FromRgb(244, 200, 106));
    private static readonly WpfBrush DangerBrush = new SolidColorBrush(WpfColor.FromRgb(255, 123, 136));
    private static readonly Duration PageDuration = new(TimeSpan.FromMilliseconds(180));
    private readonly RaffertyController _controller;
    private readonly SettingsService _settingsService;
    private readonly LatencyTester _latencyTester = new();
    private readonly UpdateService _updateService = new();
    private readonly JsonFileStore<UpdateState> _updateStateStore = new(AppPaths.UpdateStateFile);
    private readonly Forms.NotifyIcon _tray;
    private readonly CancellationTokenSource _lifetime = new();
    private UserSettings _settings;
    private EngineSnapshot? _status;
    private bool _exitRequested;
    private bool _loadingSettings;
    private IReadOnlyList<Strategy> _strategies = [];
    private AppPage _currentPage = AppPage.Dashboard;
    private UpdateManifest? _availableUpdate;

    internal MainWindow(RaffertyController controller, SettingsService settingsService, UserSettings settings)
    {
        _controller = controller;
        _settingsService = settingsService;
        _settings = settings;
        InitializeComponent();
        CurrentVersionText.Text = AppVersion.Display;
        ApplySettingsToControls();
        _tray = CreateTrayIcon();
        Loaded += async (_, _) =>
        {
            await LoadStrategiesAsync();
            RefreshRuntimeDetails();
            UpdateStatus(_controller.Status);
            if (_settings.AutoCheckUpdates) await CheckForUpdatesAsync(false);
        };
    }

    internal async Task StartEnabledAsync()
    {
        if (_status?.EngineRunning == true) return;
        if (_settings.ManualMode)
        {
            await ApplySelectedStrategyAsync(_settings.ManualStrategyId);
        }
        else
        {
            await ToggleAsync();
        }
    }

    internal async Task RenderPagesForTestAsync(string directory, string? language = null)
    {
        if (language is not null) await ChangeLanguageAsync(language);
        if (_strategies.Count == 0) await LoadStrategiesAsync();
        Directory.CreateDirectory(directory);
        UpdateLayout();
        RenderWindow(Path.Combine(directory, "dashboard-initial.png"));
        await TestLatencyAsync();
        UpdateLayout();
        RenderWindow(Path.Combine(directory, "dashboard-latency.png"));
        _settings = _settings with { ManualMode = true };
        ApplySettingsToControls();
        ShowSettings();
        await Task.Delay(240);
        UpdateLayout();
        RenderWindow(Path.Combine(directory, "settings.png"));
        foreach (var scale in new[] { 1d, 1.25d, 1.5d })
            RenderWindow(Path.Combine(directory, $"settings-{scale * 100:0}.png"), scale);
        SettingsScrollViewer.ScrollToEnd();
        UpdateLayout();
        RenderWindow(Path.Combine(directory, "settings-updates.png"));
        SettingsScrollViewer.ScrollToHome();
        Navigate(AppPage.Advanced);
        var previewStrategy = SelectedStrategyId() ?? _settings.ManualStrategyId;
        AdvancedStrategyText.Text = StrategyNames.DisplayName(previewStrategy);
        CommandLineText.Text = await _controller.GetCommandLineAsync(previewStrategy, _lifetime.Token);
        await Task.Delay(240);
        UpdateLayout();
        RenderWindow(Path.Combine(directory, "advanced.png"));
        foreach (var scale in new[] { 1d, 1.25d, 1.5d })
            RenderWindow(Path.Combine(directory, $"advanced-{scale * 100:0}.png"), scale);
        var diagnostics = new DiagnosticsWindow(Localization.T("Diagnostics"), Localization.T("DiagnosticsDescription"), "Internet: Success — HTTP 204\nYouTube: Success — TCP 443\nDiscord API: Success — HTTP 200\nDiscord Voice / STUN: Success — UDP response received") { Owner = this };
        diagnostics.Show();
        diagnostics.RenderForTest(Path.Combine(directory, "diagnostics.png"));
        diagnostics.Close();
        var logs = new DiagnosticsWindow(Localization.T("Logs"), Localization.T("LogsDescription"), "Engine started\nStrategy selected\nYouTube reachable\nDiscord reachable") { Owner = this };
        logs.Show();
        logs.RenderForTest(Path.Combine(directory, "logs.png"));
        logs.Close();
    }

    internal void CloseForTest()
    {
        _exitRequested = true;
        _tray.Visible = false;
        _tray.Dispose();
        Close();
    }

    private void RenderWindow(string path, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * scale), (int)Math.Ceiling(ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var tray = new Forms.NotifyIcon { Text = Localization.T("TrayDisabled"), Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Visible = true };
        tray.DoubleClick += (_, _) => RestoreWindow();
        BuildTrayMenu(tray);
        return tray;
    }

    private void BuildTrayMenu(Forms.NotifyIcon tray)
    {
        tray.ContextMenuStrip?.Dispose();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Localization.T("OpenApp"), null, (_, _) => RestoreWindow());
        menu.Items.Add(Localization.T("Enable"), null, async (_, _) => { if (_status?.EngineRunning != true) await ToggleAsync(); });
        menu.Items.Add(Localization.T("Disable"), null, async (_, _) => { if (_status?.EngineRunning == true) await ToggleAsync(); });
        menu.Items.Add(Localization.T("Optimize"), null, async (_, _) => await ReoptimizeAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Localization.T("Exit"), null, async (_, _) => await ExitApplicationAsync());
        tray.ContextMenuStrip = menu;
    }

    private async Task ToggleAsync()
    {
        SetBusy(true, _status?.EngineRunning == true ? Localization.T("Disabling") : Localization.T("OptimizingConnection"));
        try
        {
            EngineSnapshot status;
            if (_status?.EngineRunning == true)
            {
                status = await _controller.DisableAsync(_lifetime.Token);
                ActivityText.Text = Localization.T("ProtectionDisabled");
            }
            else
            {
                SetCheckingState();
                status = _settings.ManualMode
                    ? await _controller.ApplyStrategyAsync(_settings.ManualStrategyId, _lifetime.Token)
                    : await _controller.EnableAsync(
                        new Progress<string>(message => ActivityText.Text = LocalizeProgress(message)),
                        _lifetime.Token,
                        _settings.CheckYouTube,
                        _settings.CheckDiscord,
                        _settings.CheckVoice,
                        _settings.AutoFindOnFailure,
                        _settings.RecheckOnStartup,
                        _settings.PreferredEngine);
                ActivityText.Text = Localization.T("ConnectionOptimized");
                if (_settings.Notifications && status.DriverActive && status.StrategyApplied && status.ConnectivityVerified)
                    _tray.ShowBalloonTip(1200, Localization.T("AppName"), Localization.T("ConnectionProtected"), Forms.ToolTipIcon.Info);
            }
            UpdateStatus(status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ActivityText.Text = exception.Message;
            StatusTitle.Text = Localization.T("ConnectionProblem");
            StatusSubtitle.Text = Localization.T("OpenDiagnostics");
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("AppName"), MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatus(_controller.Status);
        }
        finally { SetBusy(false, ActivityText.Text); }
    }

    private async Task ReoptimizeAsync()
    {
        ShowDashboard();
        SetBusy(true, Localization.T("FindingConfiguration"));
        SetCheckingState();
        try
        {
            var status = await _controller.ReoptimizeAsync(
                new Progress<string>(message => ActivityText.Text = LocalizeProgress(message)),
                _lifetime.Token,
                _settings.CheckYouTube,
                _settings.CheckDiscord,
                _settings.CheckVoice,
                _settings.PreferredEngine,
                deepSearch: true);
            ActivityText.Text = Localization.T("ConnectionOptimized");
            UpdateStatus(status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ActivityText.Text = exception.Message;
            var best = _controller.LastOptimization;
            if (best is { Success: false, SelectedStrategyId: not null })
            {
                var choice = System.Windows.MessageBox.Show(this,
                    $"{exception.Message}\n\nBest result: {StrategyNames.DisplayName(best.SelectedStrategyId)}\n\nApply this strategy manually?",
                    Localization.T("AppName"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.Yes)
                {
                    await ApplySelectedStrategyAsync(best.SelectedStrategyId);
                    return;
                }
            }
            else
            {
                System.Windows.MessageBox.Show(this, exception.Message, Localization.T("AppName"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { SetBusy(false, ActivityText.Text); }
    }

    private async Task RunDiagnosticsAsync()
    {
        SetBusy(true, Localization.T("CheckingServices"));
        try
        {
            var diagnostics = await _controller.RunDiagnosticsAsync(_lifetime.Token);
            UpdateStatus(_controller.Status);
            var text = string.Join(Environment.NewLine, diagnostics.Select(result => $"{result.Name}: {result.State} — {result.Detail}"));
            new DiagnosticsWindow(Localization.T("Diagnostics"), Localization.T("DiagnosticsDescription"), text) { Owner = this }.ShowDialog();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("Diagnostics"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false, Localization.T("DiagnosticsCompleted")); }
    }

    private async Task TestLatencyAsync()
    {
        LatencyButton.IsEnabled = false;
        LatencyButton.Content = Localization.T("Testing");
        LastTestText.Text = Localization.T("TestingEndpoints");
        try
        {
            var youtubeTask = _latencyTester.TestYouTubeAsync(_lifetime.Token);
            var discordTask = _latencyTester.TestDiscordAsync(_lifetime.Token);
            var voiceTask = _latencyTester.TestDiscordVoiceAsync(_lifetime.Token);
            await Task.WhenAll(youtubeTask, discordTask, voiceTask);
            ShowLatency(YoutubeLatency, await youtubeTask);
            ShowLatency(DiscordLatency, await discordTask);
            ShowLatency(VoiceLatency, await voiceTask);
            LastTestText.Text = string.Format(Localization.T("LastTested"), DateTime.Now.ToString("HH:mm"));
        }
        catch (OperationCanceledException) { LastTestText.Text = Localization.T("TestCancelled"); }
        finally { LatencyButton.Content = Localization.T("TestLatency"); LatencyButton.IsEnabled = true; }
    }

    private static void ShowLatency(TextBlock target, ServiceLatencyResult result)
    {
        if (!result.Success || result.LatencyMs is null)
        {
            target.Text = Localization.T("Failed");
            target.Foreground = DangerBrush;
            target.ToolTip = result.Error;
            return;
        }
        var value = result.LatencyMs.Value;
        target.Text = $"{Math.Round(value):0} ms";
        target.Foreground = value switch { < 60 => SuccessBrush, < 120 => (WpfBrush)System.Windows.Application.Current.FindResource("PrimaryTextBrush"), < 200 => WarningBrush, _ => DangerBrush };
        target.ToolTip = null;
    }

    private static string LocalizeProgress(string message)
    {
        if (message.StartsWith("Checking saved", StringComparison.OrdinalIgnoreCase)) return Localization.T("CheckingSavedConfiguration");
        if (message.StartsWith("Finding", StringComparison.OrdinalIgnoreCase)) return Localization.T("FindingConfiguration");
        return message;
    }

    private void UpdateStatus(EngineSnapshot status)
    {
        _status = status;
        var active = status.EngineRunning;
        var verified = active && status.IsAdministrator && status.DriverActive && status.StrategyApplied && status.ConnectivityVerified;
        StatusTitle.Text = Localization.T(verified ? "ProtectedTitle" : active ? "ActiveTitle" : "ReadyTitle");
        StatusSubtitle.Text = Localization.T(verified ? "ProtectedSubtitle" : active ? "ActiveSubtitle" : "ReadySubtitle");
        ProtectionText.Text = Localization.T(verified ? "Protected" : active ? "ActiveUnverified" : "NotActive");
        ProtectionText.Foreground = verified ? SuccessBrush : active ? WarningBrush : (WpfBrush)FindResource("MutedTextBrush");
        PowerButton.Content = Localization.T(active ? "Disable" : "Enable");
        PowerButton.Background = active ? new SolidColorBrush(WpfColor.FromRgb(36, 74, 67)) : new SolidColorBrush(WpfColor.FromRgb(23, 26, 37));
        PowerButton.BorderBrush = active ? SuccessBrush : new SolidColorBrush(WpfColor.FromRgb(81, 71, 154));
        StrategyText.Text = StrategyNames.DisplayName(status.StrategyId ?? _settings.ManualStrategyId);
        var visibleEngine = status.EngineType switch
        {
            EngineType.Classic => Localization.T("EngineClassic"),
            EngineType.NextGen => Localization.T("EngineNextGen"),
            _ => Localization.T("EngineAutomatic")
        };
        ModeText.Text = $"{Localization.T(_settings.ManualMode ? "ManualMode" : "AutomaticMode")} • {visibleEngine}";
        AdvancedStrategyText.Text = StrategyNames.DisplayName(status.StrategyId ?? SelectedStrategyId() ?? _settings.ManualStrategyId);
        var engineName = status.EngineType switch { EngineType.NextGen => "Next-gen (winws2)", EngineType.Classic => "Classic (winws)", _ => Localization.T("EngineAutomatic") };
        AdvancedEngineText.Text = $"{engineName} • {(active ? "Running" : "Stopped")}";
        AdvancedLuaText.Text = status.EngineType == EngineType.NextGen ? "zapret-lib + antidpi" : "—";
        AdvancedDriverText.Text = status.DriverActive ? "Loaded" : "Not loaded";
        AdvancedPidText.Text = status.ProcessId?.ToString() ?? "—";
        AdvancedWorkingDirectoryText.Text = _controller.EngineWorkingDirectory;
        AdvancedRuntimeText.Text = _controller.RuntimeDirectory;
        CommandLineText.Text = status.CommandLine ?? string.Empty;
        SetReachability(YoutubeState, YoutubeDot, status.Reachability.YouTube);
        SetReachability(DiscordState, DiscordDot, status.Reachability.Discord);
        SetReachability(VoiceState, VoiceDot, status.Reachability.DiscordVoice);
        _tray.Text = Localization.T(verified ? "TrayProtected" : active ? "TrayUnverified" : "TrayDisabled");
    }

    private void SetCheckingState()
    {
        StatusTitle.Text = Localization.T("FindingConfiguration");
        StatusSubtitle.Text = Localization.T("UsuallyMoment");
        YoutubeState.Text = Localization.T("Testing"); DiscordState.Text = Localization.T("Waiting"); VoiceState.Text = Localization.T("Waiting");
        YoutubeDot.Fill = DiscordDot.Fill = VoiceDot.Fill = WarningBrush;
    }

    private static void SetReachability(TextBlock text, System.Windows.Shapes.Ellipse dot, ServiceReachability state)
    {
        text.Text = Localization.T(state switch { ServiceReachability.Working => "Working", ServiceReachability.Degraded => "Limited", ServiceReachability.Unavailable => "Unavailable", _ => "Waiting" });
        dot.Fill = state switch { ServiceReachability.Working => SuccessBrush, ServiceReachability.Degraded => WarningBrush, ServiceReachability.Unavailable => DangerBrush, _ => IdleBrush };
    }

    private void ApplySettingsToControls()
    {
        _loadingSettings = true;
        StartWithWindowsToggle.IsChecked = _settings.StartWithWindows;
        StartEnabledToggle.IsChecked = _settings.StartEnabled;
        MinimizeToTrayToggle.IsChecked = _settings.MinimizeToTray;
        NotificationsToggle.IsChecked = _settings.Notifications;
        CheckYouTubeBox.IsChecked = _settings.CheckYouTube;
        CheckDiscordBox.IsChecked = _settings.CheckDiscord;
        CheckVoiceBox.IsChecked = _settings.CheckVoice;
        AutoFindOnFailureToggle.IsChecked = _settings.AutoFindOnFailure;
        RecheckOnStartupToggle.IsChecked = _settings.RecheckOnStartup;
        AutoCheckUpdatesToggle.IsChecked = _settings.AutoCheckUpdates;
        GameFilterToggle.IsChecked = _settings.GameFilterEnabled;
        IpSetModeSelector.SelectedIndex = _settings.IpSetMode switch
        {
            IpSetMode.None => 0,
            IpSetMode.Any => 2,
            _ => 1
        };
        ModeSelector.SelectedIndex = _settings.ManualMode ? 1 : 0;
        EngineSelector.SelectedIndex = _settings.PreferredEngine switch { EngineType.Classic => 1, EngineType.NextGen => 2, _ => 0 };
        ManualStrategyPanel.Visibility = _settings.ManualMode ? Visibility.Visible : Visibility.Collapsed;
        LanguageSelector.SelectedIndex = Localization.Normalize(_settings.Language) == Localization.EnglishLanguage ? 1 : 0;
        _loadingSettings = false;
    }

    private async Task SaveSettingsAsync()
    {
        var previous = _settings;
        _settings = _settings with
        {
            StartWithWindows = StartWithWindowsToggle.IsChecked == true,
            StartEnabled = StartEnabledToggle.IsChecked == true,
            MinimizeToTray = MinimizeToTrayToggle.IsChecked == true,
            Notifications = NotificationsToggle.IsChecked == true,
            ManualMode = ModeSelector.SelectedIndex == 1,
            ManualStrategyId = SelectedStrategyId() ?? _settings.ManualStrategyId,
            PreferredEngine = EngineSelector.SelectedIndex switch { 1 => EngineType.Classic, 2 => EngineType.NextGen, _ => EngineType.Auto },
            CheckYouTube = CheckYouTubeBox.IsChecked == true,
            CheckDiscord = CheckDiscordBox.IsChecked == true,
            CheckVoice = CheckVoiceBox.IsChecked == true,
            AutoFindOnFailure = AutoFindOnFailureToggle.IsChecked == true,
            RecheckOnStartup = RecheckOnStartupToggle.IsChecked == true,
            AutoCheckUpdates = AutoCheckUpdatesToggle.IsChecked == true,
            GameFilterEnabled = GameFilterToggle.IsChecked == true,
            IpSetMode = IpSetModeSelector.SelectedIndex switch
            {
                0 => IpSetMode.None,
                2 => IpSetMode.Any,
                _ => IpSetMode.Loaded
            }
        };
        try { await _settingsService.SaveAsync(_settings, _lifetime.Token); }
        catch (Exception exception)
        {
            _settings = previous;
            ApplySettingsToControls();
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("SettingsErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadStrategiesAsync(bool reload = false)
    {
        _strategies = reload
            ? await _controller.ReloadStrategiesAsync(_lifetime.Token)
            : await _controller.GetStrategiesAsync(_lifetime.Token);
        _loadingSettings = true;
        PopulateStrategySelector();
        _loadingSettings = false;
    }

    private void PopulateStrategySelector()
    {
        var selectedId = SelectedStrategyId() ?? _settings.ManualStrategyId;
        var preferred = EngineSelector.SelectedIndex switch { 1 => EngineType.Classic, 2 => EngineType.NextGen, _ => EngineType.Auto };
        var visible = _strategies.Where(strategy => preferred == EngineType.Auto || strategy.EngineType == preferred).ToArray();
        StrategySelector.Items.Clear();
        foreach (var strategy in visible)
        {
            StrategySelector.Items.Add(new ComboBoxItem
            {
                Tag = strategy.Id,
                Content = StrategyNames.DisplayName(strategy)
            });
        }
        var selectedIndex = visible.ToList().FindIndex(strategy =>
            string.Equals(strategy.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        StrategySelector.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
    }

    private EngineRuntimeOptions RuntimeOptionsFromSettings() => new(
        _settings.IpSetMode,
        _settings.GameFilterEnabled,
        _settings.GameFilterTcp,
        _settings.GameFilterUdp);

    private void RefreshRuntimeDetails()
    {
        AdvancedIpSetText.Text = _settings.IpSetMode switch
        {
            IpSetMode.None => $"{Localization.T("IpSetOff")} (none)",
            IpSetMode.Any => $"{Localization.T("IpSetAny")} (any)",
            _ => $"{Localization.T("IpSetLoaded")} (loaded)"
        };
        AdvancedGameFilterText.Text = _settings.GameFilterEnabled
            ? $"TCP {_settings.GameFilterTcp}; UDP {_settings.GameFilterUdp}"
            : Localization.T("GameFilterDisabled");
        CustomDomainsCountText.Text = string.Format(Localization.T("DomainCount"), CountListEntries(Path.Combine(AppPaths.ListsDirectory, "list-general-user.txt")));
        ExcludedDomainsCountText.Text = string.Format(Localization.T("DomainCount"), CountListEntries(Path.Combine(AppPaths.ListsDirectory, "list-exclude-user.txt")));
    }

    private static int CountListEntries(string path) => File.Exists(path)
        ? File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
        : 0;

    private string? SelectedStrategyId() =>
        StrategySelector.SelectedItem is ComboBoxItem { Tag: string id } ? id : null;

    private async Task ApplySelectedStrategyAsync(string? strategyId = null)
    {
        strategyId ??= SelectedStrategyId();
        if (string.IsNullOrWhiteSpace(strategyId)) return;

        SetBusy(true, $"Applying {StrategyNames.DisplayName(strategyId)}...");
        try
        {
            _settings = _settings with { ManualMode = true, ManualStrategyId = strategyId };
            ModeSelector.SelectedIndex = 1;
            ManualStrategyPanel.Visibility = Visibility.Visible;
            await _settingsService.SaveAsync(_settings, _lifetime.Token);
            var status = await _controller.ApplyStrategyAsync(strategyId, _lifetime.Token);
            UpdateStatus(status);
            ActivityText.Text = $"{StrategyNames.DisplayName(strategyId)}: Engine OK, WinDivert OK";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ActivityText.Text = exception.Message;
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("SettingsErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatus(_controller.Status);
        }
        finally
        {
            SetBusy(false, ActivityText.Text);
        }
    }

    private async void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        ManualStrategyPanel.Visibility = ModeSelector.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        await SaveSettingsAsync();
        UpdateStatus(_controller.Status);
    }

    private async void EngineSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _loadingSettings = true;
        PopulateStrategySelector();
        _loadingSettings = false;
        await SaveSettingsAsync();
        UpdateStatus(_controller.Status);
    }

    private async void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || LanguageSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string language) return;
        await ChangeLanguageAsync(language);
    }

    private async Task ChangeLanguageAsync(string language)
    {
        language = Localization.Normalize(language);
        _settings = _settings with { Language = language };
        Localization.Apply(language);
        _loadingSettings = true;
        LanguageSelector.SelectedIndex = language == Localization.EnglishLanguage ? 1 : 0;
        _loadingSettings = false;
        UpdateStatus(_status ?? _controller.Status);
        BuildTrayMenu(_tray);
        try { await _settingsService.SaveAsync(_settings, _lifetime.Token); }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("SettingsErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowSettings() => Navigate(AppPage.Settings);
    private void ShowDashboard() => Navigate(AppPage.Dashboard);

    private void Navigate(AppPage page)
    {
        if (page == _currentPage) return;
        var from = PageGrid(_currentPage);
        var to = PageGrid(page);
        var offset = page > _currentPage ? 24 : -24;
        _currentPage = page;
        SwitchPage(from, to, offset);
    }

    private Grid PageGrid(AppPage page) => page switch
    {
        AppPage.Dashboard => DashboardPage,
        AppPage.Settings => SettingsPage,
        AppPage.Advanced => AdvancedPage,
        _ => DashboardPage
    };

    private static void SwitchPage(Grid from, Grid to, double offset)
    {
        to.Visibility = Visibility.Visible;
        to.Opacity = 0;
        if (to.RenderTransform is TranslateTransform incoming) incoming.X = offset;
        var fadeIn = new DoubleAnimation(0, 1, PageDuration);
        var slideIn = new DoubleAnimation(offset, 0, PageDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var fadeOut = new DoubleAnimation(1, 0, PageDuration);
        fadeOut.Completed += (_, _) => { from.Visibility = Visibility.Collapsed; from.Opacity = 1; };
        from.BeginAnimation(OpacityProperty, fadeOut);
        to.BeginAnimation(OpacityProperty, fadeIn);
        (to.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private void SetBusy(bool busy, string message) { PowerButton.IsEnabled = !busy; LatencyButton.IsEnabled = !busy; ApplyStrategyButton.IsEnabled = !busy; ActivityProgress.IsIndeterminate = busy; ActivityProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; ActivityText.Text = message; }
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; Activate(); }

    private async Task ExitApplicationAsync()
    {
        _exitRequested = true;
        _lifetime.Cancel();
        _latencyTester.Dispose();
        _updateService.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        await Task.Yield();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (_settings.Notifications) _tray.ShowBalloonTip(1400, Localization.T("AppName"), Localization.T("StillRunning"), Forms.ToolTipIcon.Info);
        }
        base.OnClosing(e);
    }

    private async void PowerButton_Click(object sender, RoutedEventArgs e) => await ToggleAsync();
    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e) => await RunDiagnosticsAsync();
    private async void ParityTestButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, Localization.T("ParityTestRunning"));
        try
        {
            var text = await _controller.RunReferenceParityTestAsync(_lifetime.Token);
            UpdateStatus(_controller.Status);
            new DiagnosticsWindow(Localization.T("ParityTest"), Localization.T("ParityTestDescription"), text) { Owner = this }.ShowDialog();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("ParityTest"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false, Localization.T("DiagnosticsCompleted")); }
    }
    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, Localization.T("CheckingServices"));
        try
        {
            var path = await _controller.ExportDiagnosticsAsync(token: _lifetime.Token);
            UpdateStatus(_controller.Status);
            System.Windows.MessageBox.Show(this, string.Format(Localization.T("DiagnosticsExported").Replace("\\n", Environment.NewLine), path), Localization.T("ExportDiagnostics"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("ExportDiagnostics"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false, Localization.T("DiagnosticsCompleted")); }
    }
    private async void ReoptimizeButton_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { ManualMode = false };
        _loadingSettings = true;
        ModeSelector.SelectedIndex = 0;
        ManualStrategyPanel.Visibility = Visibility.Collapsed;
        _loadingSettings = false;
        await _settingsService.SaveAsync(_settings, _lifetime.Token);
        await ReoptimizeAsync();
    }
    private async void LatencyButton_Click(object sender, RoutedEventArgs e) => await TestLatencyAsync();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void BackButton_Click(object sender, RoutedEventArgs e) => ShowDashboard();
    private async void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        if (sender is ToggleButton toggle
            && (ReferenceEquals(toggle, CheckYouTubeBox) || ReferenceEquals(toggle, CheckDiscordBox) || ReferenceEquals(toggle, CheckVoiceBox))
            && CheckYouTubeBox.IsChecked != true && CheckDiscordBox.IsChecked != true && CheckVoiceBox.IsChecked != true)
        {
            toggle.IsChecked = true;
            return;
        }
        await SaveSettingsAsync();
    }
    private async void RuntimeSetting_Changed(object sender, RoutedEventArgs e) => await ApplyRuntimeSettingsAsync();
    private async void RuntimeSetting_Changed(object sender, SelectionChangedEventArgs e) => await ApplyRuntimeSettingsAsync();

    private async Task ApplyRuntimeSettingsAsync()
    {
        if (_loadingSettings) return;
        var previous = _settings;
        await SaveSettingsAsync();
        try
        {
            SetBusy(true, Localization.T("ApplyingRuntimeSettings"));
            var status = await _controller.ConfigureRuntimeAsync(RuntimeOptionsFromSettings(), true,
                _settings.CheckYouTube, _settings.CheckDiscord, _settings.CheckVoice, _lifetime.Token);
            RefreshRuntimeDetails();
            UpdateStatus(status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _settings = previous;
            ApplySettingsToControls();
            await _settingsService.SaveAsync(_settings, _lifetime.Token);
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("SettingsErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false, ActivityText.Text); }
    }
    private async void OpenAdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        Navigate(AppPage.Advanced);
        if (string.IsNullOrWhiteSpace(CommandLineText.Text))
        {
            var strategyId = _controller.Status.StrategyId ?? SelectedStrategyId() ?? _settings.ManualStrategyId;
            try { CommandLineText.Text = await _controller.GetCommandLineAsync(strategyId, _lifetime.Token); }
            catch (Exception exception) { CommandLineText.Text = exception.Message; }
        }
    }
    private void AdvancedBackButton_Click(object sender, RoutedEventArgs e) => Navigate(AppPage.Settings);
    private async void ApplyStrategyButton_Click(object sender, RoutedEventArgs e) => await ApplySelectedStrategyAsync();
    private void CopyCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(CommandLineText.Text)) System.Windows.Clipboard.SetText(CommandLineText.Text);
    }
    private async void StrategyListButton_Click(object sender, RoutedEventArgs e)
    {
        var scores = _controller.LastOptimization?.Scores.ToDictionary(score => score.StrategyId, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, StrategyScore>(StringComparer.OrdinalIgnoreCase);
        var window = new StrategyListWindow(_strategies, scores) { Owner = this };
        if (window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.SelectedStrategyId))
        {
            await ApplySelectedStrategyAsync(window.SelectedStrategyId);
        }
    }
    private async void ReloadStrategiesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadStrategiesAsync(true);
            ActivityText.Text = $"Loaded {_strategies.Count} strategies.";
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("StrategyList"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async void ImportStrategyButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.T("ImportStrategy"),
            Filter = "Windows batch strategy (*.bat)|*.bat",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = await _controller.ImportStrategyAsync(dialog.FileName, _lifetime.Token);
            await LoadStrategiesAsync();
            ActivityText.Text = $"Imported {StrategyNames.DisplayName(imported)}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Windows.MessageBox.Show(this, exception.Message, Localization.T("ImportStrategy"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void EditCustomDomainsButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextFile(Path.Combine(AppPaths.ListsDirectory, "list-general-user.txt"));
    private void EditExcludedDomainsButton_Click(object sender, RoutedEventArgs e) =>
        OpenTextFile(Path.Combine(AppPaths.ListsDirectory, "list-exclude-user.txt"));
    private static void OpenTextFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }
    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var notices = Directory.Exists(AppPaths.LicensesDirectory)
            ? string.Join(Environment.NewLine + Environment.NewLine, Directory.GetFiles(AppPaths.LicensesDirectory, "*.txt")
                .OrderBy(Path.GetFileName).Select(path => $"===== {Path.GetFileName(path)} ====={Environment.NewLine}{File.ReadAllText(path)}"))
            : Localization.T("NoLogs");
        var heading = Localization.T("About");
        var description = $"Rafferty {AppVersion.Display} • {Localization.T("AboutText").Replace("\\n", " • ")}";
        new DiagnosticsWindow(heading, description, notices) { Owner = this }.ShowDialog();
    }
    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppPaths.LogsDirectory, "rafferty.log");
        var text = File.Exists(path) ? File.ReadAllText(path) : Localization.T("NoLogs");
        new DiagnosticsWindow(Localization.T("Logs"), Localization.T("LogsDescription"), text) { Owner = this }.ShowDialog();
    }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);

    private async Task CheckForUpdatesAsync(bool force)
    {
        UpdateState state;
        try { state = await _updateStateStore.LoadAsync(_lifetime.Token) ?? new UpdateState(); }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            state = new UpdateState();
        }
        if (!force && state.LastCheckedAt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
        {
            if (state.LatestManifest is { } cached && UpdateService.ParseVersion(cached.Version) > AppVersion.Current)
            {
                ShowAvailableUpdate(cached);
            }
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = Localization.T("CheckingUpdates");
        try
        {
            var result = await _updateService.CheckAsync(token: _lifetime.Token);
            await _updateStateStore.SaveAsync(new(DateTimeOffset.UtcNow, result.Manifest), _lifetime.Token);
            if (result.UpdateAvailable)
            {
                ShowAvailableUpdate(result.Manifest);
                if (!force && _settings.Notifications)
                {
                    _tray.ShowBalloonTip(3000, Localization.T("AppName"), string.Format(Localization.T("UpdateAvailable"), result.Manifest.Version), Forms.ToolTipIcon.Info);
                }
            }
            else
            {
                _availableUpdate = null;
                UpdateNowButton.Visibility = Visibility.Collapsed;
                UpdateLaterButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = Localization.T("LatestVersionInstalled");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _updateStateStore.SaveAsync(new(DateTimeOffset.UtcNow, state.LatestManifest), _lifetime.Token);
            UpdateStatusText.Text = string.Format(Localization.T("UpdateError"), exception.Message);
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private void ShowAvailableUpdate(UpdateManifest manifest)
    {
        _availableUpdate = manifest;
        UpdateStatusText.Text = $"{string.Format(Localization.T("UpdateAvailable"), manifest.Version)}\n\n{manifest.ReleaseNotes}";
        UpdateNowButton.Visibility = Visibility.Visible;
        UpdateLaterButton.Visibility = Visibility.Visible;
    }

    private void UpdateLaterButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateNowButton.Visibility = Visibility.Collapsed;
        UpdateLaterButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = Localization.T("UpdateDeferred");
    }

    private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        CheckUpdatesButton.IsEnabled = UpdateNowButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var versionDirectory = Path.Combine(AppPaths.UpdatesDirectory, _availableUpdate.Version);
            var downloaded = Path.Combine(versionDirectory, "Rafferty.new.exe");
            var progress = new Progress<double>(value =>
            {
                UpdateProgress.Value = value;
                UpdateStatusText.Text = string.Format(Localization.T("DownloadingUpdate"), value);
            });
            await _updateService.DownloadAsync(_availableUpdate, downloaded, progress, _lifetime.Token);
            UpdateStatusText.Text = Localization.T("InstallingUpdate");
            await _controller.DisableAsync(_lifetime.Token);
            SelfUpdateInstaller.StartUpdater(downloaded);
            await ExitApplicationAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UpdateStatusText.Text = string.Format(Localization.T("UpdateError"), exception.Message);
            CheckUpdatesButton.IsEnabled = UpdateNowButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }
}
