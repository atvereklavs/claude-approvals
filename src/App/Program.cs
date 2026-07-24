using System.IO;
using System.Windows;
using ClaudeApprovals.Core.Models;
using ClaudeApprovals.Core.Server;
using ClaudeApprovals.Core.State;
using WinForms = System.Windows.Forms;

namespace ClaudeApprovals.App;

/// <summary>
/// Thin WPF shell over the cross-platform Core: tray icon + top-center popup.
/// All decision logic lives in Core; this file only wires UI events.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var store = new RequestStore();
        store.SessionRules = new SessionRuleStore();
        store.ProjectRules = new ProjectRuleStore();
        store.OnOutcome += (req, decision, source) => DecisionLog.Append(req, decision, source);

        var port = int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_APPROVALS_PORT"), out var p) ? p : 8790;
        var token = LoadToken();
        var server = new ApprovalServer(store, port, token);

        var popup = new PopupWindow(store);

        store.OnEnqueue += _ => app.Dispatcher.BeginInvoke(popup.ShowNext);
        store.OnResolve += (_, _, _) => app.Dispatcher.BeginInvoke(popup.ShowNext);

        // Session notifications: balloon tips for finished / waiting sessions.
        var notificationsOn = true;

        // Tray icon (WinForms NotifyIcon — in-box, no dependency).
        var tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Visible = true,
            Text = "Claude Approvals",
        };
        // Balloons for passive session events (suppressed while a card is up —
        // the popup itself is the notification then).
        server.OnNotify += payload => app.Dispatcher.BeginInvoke(() =>
        {
            if (!notificationsOn || store.PendingCount > 0) return;
            var label = payload.SessionLabel;
            switch (payload.HookEventName)
            {
                case "Stop":
                    tray!.ShowBalloonTip(3000, "Claude Code", $"{label} finished", WinForms.ToolTipIcon.Info);
                    break;
                case "Notification" when payload.NotificationType == "idle_prompt":
                    tray!.ShowBalloonTip(3000, "Claude Code", $"{label} is waiting for you", WinForms.ToolTipIcon.Warning);
                    break;
            }
        });

        var menu = new WinForms.ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add($"Pending: {store.PendingCount}").Enabled = false;
            menu.Items.Add($"Port: {server.Port}").Enabled = false;
            menu.Items.Add(new WinForms.ToolStripSeparator());
            var notif = new WinForms.ToolStripMenuItem("Session notifications")
            { Checked = notificationsOn, CheckOnClick = true };
            notif.CheckedChanged += (_, _) => notificationsOn = notif.Checked;
            menu.Items.Add(notif);
            menu.Items.Add("Clear session rules", null, (_, _) => store.SessionRules?.ClearAll());
            menu.Items.Add($"Clear project rules ({store.ProjectRules?.Count ?? 0})", null,
                (_, _) => store.ProjectRules?.ClearAll());
            var log = menu.Items.Add("Open decision log", null, (_, _) =>
            {
                if (File.Exists(DecisionLog.LogPath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DecisionLog.LogPath)
                    { UseShellExecute = true });
            });
            log.Enabled = File.Exists(DecisionLog.LogPath);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) =>
            {
                tray.Visible = false;
                server.Dispose();
                app.Shutdown();
            });
        };
        tray.ContextMenuStrip = menu;

        app.Run();
        tray.Dispose();
    }

    private static string? LoadToken()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_APPROVALS_TOKEN");
        if (!string.IsNullOrEmpty(env)) return env;
        try
        {
            var path = Path.Combine(ConfigDir.Path, "token");
            if (File.Exists(path))
            {
                var t = File.ReadAllText(path).Trim();
                if (t.Length > 0) return t;
            }
        }
        catch { }
        return null;
    }
}
