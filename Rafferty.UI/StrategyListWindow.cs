using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rafferty.Core;
using Rafferty.Shared;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;

namespace Rafferty.UI;

internal sealed class StrategyListWindow : Window
{
    public string? SelectedStrategyId { get; private set; }

    public StrategyListWindow(IReadOnlyList<Strategy> strategies, IReadOnlyDictionary<string, StrategyScore> scores)
    {
        Title = Localization.T("StrategyList");
        Width = 430;
        Height = 650;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(WpfColor.FromRgb(9, 11, 16));
        Foreground = WpfBrushes.White;

        var root = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock
        {
            Text = Localization.T("StrategyList"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var stack = new StackPanel();
        foreach (var strategy in strategies)
        {
            scores.TryGetValue(strategy.Id, out var score);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var details = new StackPanel();
            details.Children.Add(new TextBlock
            {
                Text = StrategyNames.DisplayName(strategy),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            });
            details.Children.Add(new TextBlock
            {
                Text = ResultText(score),
                FontSize = 10,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(162, 168, 184)),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            row.Children.Add(details);

            var apply = new WpfButton
            {
                Content = Localization.T("Apply"),
                Tag = strategy.Id,
                Width = 90,
                Height = 32,
                Margin = new Thickness(10, 0, 0, 0)
            };
            apply.Click += (_, _) =>
            {
                SelectedStrategyId = (string)apply.Tag;
                DialogResult = true;
            };
            Grid.SetColumn(apply, 1);
            row.Children.Add(apply);

            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(17, 20, 28)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(39, 46, 59)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Child = row
            });
        }

        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private static string ResultText(StrategyScore? score)
    {
        if (score is null) return Localization.T("NotTestedResult");
        if (!score.EngineStarted) return $"ENGINE FAILED: {score.FailureReason}";
        return $"YouTube: {score.YouTube}  •  Discord: {score.Discord}  •  Voice: {score.Voice}  •  {score.Score:F0}";
    }
}
