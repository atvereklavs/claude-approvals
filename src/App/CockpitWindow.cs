using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeApprovals.Core.State;

namespace ClaudeApprovals.App;

/// <summary>
/// The session cockpit: a small always-on-top window listing every Claude Code
/// session with a state dot, label, state text, and time since last activity.
/// Opened from the tray menu (or at startup with CLAUDE_APPROVALS_COCKPIT=1,
/// used by E2E). Refreshes on registry changes + a 30s tick for relative times.
/// </summary>
public sealed class CockpitWindow : Window
{
    private readonly SessionRegistry _registry;
    private readonly StackPanel _list = new() { Margin = new Thickness(12) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };

    public CockpitWindow(SessionRegistry registry)
    {
        _registry = registry;
        Title = "Claude Sessions";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 480;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 20));

        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Content = scroll;

        Loaded += (_, _) => PositionTopRight();
        _registry.Changed += () => Dispatcher.BeginInvoke(Refresh);
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
        Refresh();

        // Hide instead of close so the tray can re-open it.
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    private void PositionTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Top + 12;
    }

    private void Refresh()
    {
        _list.Children.Clear();
        var sessions = _registry.Ordered;
        if (sessions.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No active sessions",
                Foreground = Brushes.Gray, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8),
            });
            return;
        }
        foreach (var s in sessions)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            System.Windows.Automation.AutomationProperties.SetName(
                row, $"session:{s.Label}:{s.StateLabel}");

            var dot = MakeDot(ColorFor(s.DisplayState));
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            if (s.Pending > 0)
            {
                var badge = new TextBlock
                {
                    Text = s.Pending.ToString(),
                    Foreground = Brushes.Orange, FontWeight = FontWeights.Bold, FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
            }

            row.Children.Add(new StackPanel
            {
                Margin = new Thickness(9, 0, 0, 0),
                Children =
                {
                    new TextBlock
                    {
                        Text = s.Label, Foreground = Brushes.White,
                        FontWeight = FontWeights.SemiBold, FontSize = 12,
                    },
                    new TextBlock
                    {
                        Text = $"{s.StateLabel} · {Relative(s.LastActivity)}",
                        Foreground = Brushes.Gray, FontSize = 10,
                    },
                },
            });
            _list.Children.Add(row);
        }
    }

    private static Color ColorFor(SessionState s) => s switch
    {
        SessionState.WaitingApproval => Colors.Orange,
        SessionState.WaitingInput => Colors.Gold,
        SessionState.Working => Color.FromRgb(80, 250, 123),
        _ => Colors.Gray,
    };

    private static string Relative(DateTimeOffset t)
    {
        var s = (int)(DateTimeOffset.UtcNow - t).TotalSeconds;
        if (s < 5) return "now";
        if (s < 60) return $"{s}s ago";
        if (s < 3600) return $"{s / 60}m ago";
        return $"{s / 3600}h ago";
    }

    /// <summary>A vertically-centered state dot (Ellipse is sealed in WPF).</summary>
    private static System.Windows.Shapes.Ellipse MakeDot(Color c) => new()
    {
        Width = 9,
        Height = 9,
        Fill = new SolidColorBrush(c),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
