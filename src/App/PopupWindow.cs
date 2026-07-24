using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudeApprovals.Core.Models;
using ClaudeApprovals.Core.State;

namespace ClaudeApprovals.App;

/// <summary>
/// The top-center approval popup: borderless, always-on-top, NON-ACTIVATING
/// (WS_EX_NOACTIVATE — clicking a button never steals focus from the editor).
/// Renders the front pending request: permission card (diff / command / cwd /
/// reason), AskUserQuestion answer form, or ExitPlanMode plan. Hotkeys
/// Ctrl+Alt+Y / N / U while visible.
/// </summary>
public sealed class PopupWindow : Window
{
    private readonly RequestStore _store;
    private PendingRequest? _current;

    // Ask-form state
    private readonly Dictionary<string, string> _selected = new();   // question -> chosen label(s)
    private readonly Dictionary<string, TextBox> _freeText = new();

    public PopupWindow(RequestStore store)
    {
        _store = store;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
    }

    /// <summary>Show the front pending request, or hide when the queue is empty.</summary>
    public void ShowNext()
    {
        var front = _store.Front;
        if (front is null)
        {
            UnregisterHotkeys();
            Hide();
            _current = null;
            return;
        }
        if (_current?.Id == front.Id && IsVisible) return;
        _current = front;
        _selected.Clear();
        _freeText.Clear();
        Content = BuildCard(front);
        PositionTopCenter();
        Show();
        RegisterHotkeys();
    }

    // ---------------------------------------------------------------- layout

    private void PositionTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 8;
    }

    private UIElement BuildCard(PendingRequest req)
    {
        var s = req.Summary;
        var stack = new StackPanel { Margin = new Thickness(14) };

        // Header: session label + queue badge + close (X = no input to Claude).
        var header = new DockPanel();
        var title = new TextBlock
        {
            Text = $"{req.Payload.SessionLabel}  ·  {s.Title}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = Brushes.White,
        };
        var close = MakeLinkButton("✕", () => Decide(new Decision.NoOpinion()));
        DockPanel.SetDock(close, Dock.Right);
        var behind = _store.PendingCount - 1;
        if (behind > 0)
        {
            var badge = new TextBlock
            {
                Text = $"+{behind}", Foreground = Brushes.Gray, Margin = new Thickness(8, 0, 8, 0),
            };
            DockPanel.SetDock(badge, Dock.Right);
            header.Children.Add(badge);
        }
        header.Children.Add(close);
        header.Children.Add(title);
        stack.Children.Add(header);

        if (s.Cwd is not null)
            stack.Children.Add(Muted($"📁 {s.Cwd}", 4));
        if (!string.IsNullOrEmpty(s.Reason))
            stack.Children.Add(Muted(s.Reason!, 4, italic: true));

        // Body
        if (s.Ask is not null) stack.Children.Add(BuildAskForm(s.Ask));
        else if (s.Plan is not null) stack.Children.Add(BuildScrollText(s.Plan, 300));
        else if (s.Diff is not null) stack.Children.Add(BuildDiff(s.Diff));
        else if (!string.IsNullOrEmpty(s.FullText)) stack.Children.Add(BuildScrollText(s.FullText!, 180, mono: true));

        // Buttons
        stack.Children.Add(s.Ask is not null ? BuildAskButtons(s.Ask) : BuildDecisionButtons(s));

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 18, 18, 20)),
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private UIElement BuildDiff(IReadOnlyList<DiffLine> diff)
    {
        var panel = new StackPanel();
        foreach (var line in diff)
        {
            var (prefix, fg, bg) = line.Kind switch
            {
                DiffKind.Added => ("+", Color.FromRgb(80, 250, 123), Color.FromArgb(30, 80, 250, 123)),
                DiffKind.Removed => ("-", Color.FromRgb(255, 85, 85), Color.FromArgb(30, 255, 85, 85)),
                _ => (" ", Colors.Gray, Colors.Transparent),
            };
            panel.Children.Add(new TextBlock
            {
                Text = $"{prefix} {line.Text}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = new SolidColorBrush(fg),
                Background = new SolidColorBrush(bg),
                TextWrapping = TextWrapping.NoWrap,
            });
        }
        return new ScrollViewer
        {
            Content = panel, MaxHeight = 240, Margin = new Thickness(0, 8, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private UIElement BuildScrollText(string text, double maxHeight, bool mono = false)
    {
        return new ScrollViewer
        {
            MaxHeight = maxHeight,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
                Foreground = Brushes.White,
                FontFamily = mono ? new FontFamily("Consolas") : new FontFamily("Segoe UI"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(8),
            },
        };
    }

    // ----------------------------------------------------------- ask form

    private UIElement BuildAskForm(AskContent ask)
    {
        var panel = new StackPanel();
        foreach (var q in ask.Questions)
        {
            panel.Children.Add(new TextBlock
            {
                Text = q.Text, FontWeight = FontWeights.SemiBold, FontSize = 13,
                Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 4),
            });
            foreach (var opt in q.Options)
            {
                ToggleButton toggle = q.MultiSelect
                    ? new CheckBox() : new RadioButton { GroupName = Hash(q.Text) };
                toggle.Foreground = Brushes.White;
                toggle.Margin = new Thickness(0, 2, 0, 2);
                toggle.Content = new TextBlock
                {
                    Text = string.IsNullOrEmpty(opt.Description) ? opt.Label : $"{opt.Label} - {opt.Description}",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, MaxWidth = 400,
                };
                var qText = q.Text;
                var label = opt.Label;
                toggle.Checked += (_, _) => AddSelection(qText, label, q.MultiSelect);
                toggle.Unchecked += (_, _) => RemoveSelection(qText, label);
                panel.Children.Add(toggle);
            }
            // Free-text answer with a watermark (WPF has no built-in placeholder):
            // a hint TextBlock behind a transparent TextBox, hidden while non-empty.
            var free = new TextBox
            {
                Padding = new Thickness(6),
                Background = Brushes.Transparent,
                Foreground = Brushes.White, BorderThickness = new Thickness(0),
                FontSize = 12, AcceptsReturn = false,
            };
            System.Windows.Automation.AutomationProperties.SetName(free, $"Own answer: {q.Text}");
            var hint = new TextBlock
            {
                Text = "Type your own answer…",
                Foreground = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
                FontStyle = FontStyles.Italic, FontSize = 12,
                Margin = new Thickness(9, 6, 6, 6),
                IsHitTestVisible = false,
            };
            free.TextChanged += (_, _) =>
                hint.Visibility = free.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var host = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            host.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            });
            host.Children.Add(hint);
            host.Children.Add(free);
            _freeText[q.Text] = free;
            panel.Children.Add(host);
        }
        return new ScrollViewer
        {
            Content = panel, MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private void AddSelection(string question, string label, bool multi)
    {
        if (multi && _selected.TryGetValue(question, out var existing) && existing.Length > 0)
            _selected[question] = existing + ", " + label;
        else
            _selected[question] = label;
    }

    private void RemoveSelection(string question, string label)
    {
        if (!_selected.TryGetValue(question, out var existing)) return;
        var parts = existing.Split(", ").Where(x => x != label).ToArray();
        if (parts.Length == 0) _selected.Remove(question);
        else _selected[question] = string.Join(", ", parts);
    }

    private UIElement BuildAskButtons(AskContent ask)
    {
        var row = ButtonRow();
        row.Children.Add(MakeButton("Dismiss", Color.FromRgb(200, 60, 60),
            () => Decide(new Decision.Deny("Dismissed via Claude Approvals"))));
        row.Children.Add(MakeButton("Send", Color.FromRgb(60, 160, 90), () =>
        {
            var answers = new Dictionary<string, string>();
            foreach (var q in ask.Questions)
            {
                var free = _freeText.TryGetValue(q.Text, out var tb) ? tb.Text.Trim() : "";
                var chosen = free.Length > 0 ? free
                    : _selected.TryGetValue(q.Text, out var sel) ? sel : "";
                if (chosen.Length == 0) return; // incomplete → ignore click
                answers[q.Text] = chosen;
            }
            Decide(new Decision.Answer(answers));
        }));
        return row;
    }

    private UIElement BuildDecisionButtons(ToolSummary s)
    {
        var row = ButtonRow();
        row.Children.Add(MakeButton("Deny", Color.FromRgb(200, 60, 60),
            () => Decide(new Decision.Deny("Denied via Claude Approvals"))));
        if (s.Plan is null)
        {
            row.Children.Add(MakeButton("Session", Color.FromRgb(90, 90, 100),
                () => Decide(new Decision.AllowForSession())));
            row.Children.Add(MakeButton("Always", Color.FromRgb(90, 90, 100),
                () => Decide(new Decision.AllowForProject())));
        }
        row.Children.Add(MakeButton(s.Plan is null ? "Allow" : "Approve & build",
            Color.FromRgb(60, 160, 90), () => Decide(new Decision.Allow())));
        return row;
    }

    private static UniformGrid ButtonRow() => new()
    {
        Rows = 1, Margin = new Thickness(0, 10, 0, 0),
    };

    private Button MakeButton(string text, Color bg, Action action)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(3, 0, 3, 0),
            Padding = new Thickness(0, 7, 0, 7),
            Background = new SolidColorBrush(bg),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Focusable = false,
        };
        b.Click += (_, _) => action();
        return b;
    }

    private Button MakeLinkButton(string text, Action action)
    {
        var b = new Button
        {
            Content = text, Background = Brushes.Transparent, Foreground = Brushes.Gray,
            BorderThickness = new Thickness(0), Focusable = false, Padding = new Thickness(4, 0, 4, 0),
        };
        b.Click += (_, _) => action();
        return b;
    }

    private TextBlock Muted(string text, double topMargin, bool italic = false) => new()
    {
        Text = text, Foreground = Brushes.Gray, FontSize = 11,
        FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, topMargin, 0, 0),
        MaxWidth = 440,
    };

    private void Decide(Decision decision)
    {
        if (_current is null) return;
        _store.Resolve(_current.Id, decision, DecisionSource.Popup);
    }

    private static string Hash(string s) => "q" + s.GetHashCode().ToString("X");

    // ------------------------------------------------- win32: no-activate

    private const int GwlExstyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GwlExstyle,
            GetWindowLong(hwnd, GwlExstyle) | WsExNoActivate | WsExToolWindow);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    // Hotkeys: Ctrl+Alt+Y approve, Ctrl+Alt+N deny, Ctrl+Alt+U dismiss.
    private const uint ModControl = 0x2, ModAlt = 0x1;
    private bool _hotkeysOn;

    private void RegisterHotkeys()
    {
        if (_hotkeysOn) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        RegisterHotKey(hwnd, 1, ModControl | ModAlt, 0x59); // Y
        RegisterHotKey(hwnd, 2, ModControl | ModAlt, 0x4E); // N
        RegisterHotKey(hwnd, 3, ModControl | ModAlt, 0x55); // U
        _hotkeysOn = true;
    }

    private void UnregisterHotkeys()
    {
        if (!_hotkeysOn) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hwnd, 1);
        UnregisterHotKey(hwnd, 2);
        UnregisterHotKey(hwnd, 3);
        _hotkeysOn = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (msg == WmHotkey)
        {
            switch (wParam.ToInt32())
            {
                case 1: Decide(new Decision.Allow()); handled = true; break;
                case 2: Decide(new Decision.Deny("Denied via Claude Approvals")); handled = true; break;
                case 3: Decide(new Decision.NoOpinion()); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }
}
