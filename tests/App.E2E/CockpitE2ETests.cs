using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using Xunit;

namespace ClaudeApprovals.App.E2E;

/// <summary>
/// Cockpit E2E: launch the app with CLAUDE_APPROVALS_COCKPIT=1 (window shown at
/// startup), feed real session events over /v1/notify, and assert the window
/// lists the sessions with correct states via UI Automation. Own app instance
/// on its own port so it can't interfere with the popup tests.
/// </summary>
public class CockpitE2ETests : IDisposable
{
    private const int Port = 18795;
    private const string Token = "e2e-cockpit";
    private readonly Process? _app;
    private readonly string _shots;

    public CockpitE2ETests()
    {
        _shots = Environment.GetEnvironmentVariable("E2E_SHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(_shots);
        var exe = Environment.GetEnvironmentVariable("APP_EXE");
        if (exe is null || !File.Exists(exe)) return;

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["CLAUDE_APPROVALS_PORT"] = Port.ToString();
        psi.Environment["CLAUDE_APPROVALS_TOKEN"] = Token;
        psi.Environment["CLAUDE_APPROVALS_COCKPIT"] = "1";
        psi.Environment["CLAUDE_APPROVALS_RULES"] = Path.Combine(_shots, "cockpit-rules.json");
        psi.Environment["CLAUDE_APPROVALS_LOG"] = Path.Combine(_shots, "cockpit-decisions.jsonl");
        _app = Process.Start(psi);

        using var http = new HttpClient();
        for (var i = 0; i < 40; i++)
        {
            try
            {
                if (http.GetStringAsync($"http://127.0.0.1:{Port}/v1/health").Result.Contains("\"ok\":true"))
                    return;
            }
            catch { }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("app did not become healthy");
    }

    public void Dispose()
    {
        try { _app?.Kill(entireProcessTree: true); } catch { }
    }

    private async Task Notify(string body)
    {
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/v1/notify")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Notch-Token", Token);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task CockpitListsSessionsWithStates()
    {
        Skip.If(_app is null, "APP_EXE not set");

        await Notify("""{"hook_event_name":"SessionStart","session_id":"a1","cwd":"C:\\work\\alpha"}""");
        await Notify("""{"hook_event_name":"UserPromptSubmit","session_id":"a1","cwd":"C:\\work\\alpha"}""");
        await Notify("""{"hook_event_name":"SessionStart","session_id":"b2","cwd":"C:\\work\\beta"}""");
        await Notify("""{"hook_event_name":"Notification","notification_type":"idle_prompt","session_id":"b2","cwd":"C:\\work\\beta"}""");
        Thread.Sleep(700); // let the window refresh

        using var automation = new UIA3Automation();
        var window = FindCockpit(automation);
        Capture.Screen().ToFile(Path.Combine(_shots, "cockpit.png"));

        Assert.NotNull(window.FindFirstDescendant(cf => cf.ByName("session:alpha:working")));
        Assert.NotNull(window.FindFirstDescendant(cf => cf.ByName("session:beta:waiting for you")));

        // health reflects the same registry
        using var http = new HttpClient();
        var health = await http.GetStringAsync($"http://127.0.0.1:{Port}/v1/health");
        Assert.Contains("\"sessions\":2", health);

        // SessionEnd removes the row
        await Notify("""{"hook_event_name":"SessionEnd","session_id":"b2"}""");
        Thread.Sleep(700);
        Assert.Null(window.FindFirstDescendant(cf => cf.ByName("session:beta:waiting for you")));
        Capture.Screen().ToFile(Path.Combine(_shots, "cockpit-after-end.png"));
    }

    private Window FindCockpit(UIA3Automation automation)
    {
        var pid = _app!.Id;
        for (var i = 0; i < 60; i++)
        {
            foreach (var child in automation.GetDesktop().FindAllChildren())
            {
                if (child.Properties.ProcessId.ValueOrDefault == pid
                    && child.Properties.Name.ValueOrDefault == "Claude Sessions")
                {
                    var w = child.AsWindow();
                    if (w is not null) return w;
                }
            }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("cockpit window never appeared");
    }
}
