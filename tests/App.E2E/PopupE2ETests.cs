using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using Xunit;

namespace ClaudeApprovals.App.E2E;

/// <summary>
/// True end-to-end tests on a real Windows desktop (CI runner): launch the
/// published ClaudeApprovals.exe, POST a genuine PermissionRequest payload,
/// find the popup via UI Automation, CLICK the real button, and assert the
/// parked HTTP response carries the correct decision JSON. Screenshots are
/// saved to $E2E_SHOTS for artifact upload.
///
/// Requires: APP_EXE env var → path to the published exe. Skipped otherwise.
/// </summary>
public class PopupE2ETests : IDisposable
{
    private const int Port = 18790;
    private const string Token = "e2e-token";
    private readonly Process? _app;
    private readonly string _shots;

    public PopupE2ETests()
    {
        _shots = Environment.GetEnvironmentVariable("E2E_SHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(_shots);

        var exe = Environment.GetEnvironmentVariable("APP_EXE");
        if (exe is null || !File.Exists(exe)) return;

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["CLAUDE_APPROVALS_PORT"] = Port.ToString();
        psi.Environment["CLAUDE_APPROVALS_TOKEN"] = Token;
        psi.Environment["CLAUDE_APPROVALS_RULES"] = Path.Combine(_shots, "e2e-rules.json");
        psi.Environment["CLAUDE_APPROVALS_LOG"] = Path.Combine(_shots, "e2e-decisions.jsonl");
        _app = Process.Start(psi);

        // Wait for the server to come up.
        using var http = new HttpClient();
        for (var i = 0; i < 40; i++)
        {
            try
            {
                var r = http.GetStringAsync($"http://127.0.0.1:{Port}/v1/health").Result;
                if (r.Contains("\"ok\":true")) return;
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

    private static async Task<HttpResponseMessage> PostPermission(string body)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/v1/permission")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Notch-Token", Token);
        return await http.SendAsync(req);
    }

    private Window WaitForPopup(UIA3Automation automation)
    {
        var pid = _app!.Id;
        for (var i = 0; i < 60; i++)
        {
            var desktop = automation.GetDesktop();
            foreach (var child in desktop.FindAllChildren())
            {
                if (child.Properties.ProcessId.ValueOrDefault == pid && child.ControlType == FlaUI.Core.Definitions.ControlType.Window)
                {
                    var w = child.AsWindow();
                    if (w is not null) return w;
                }
            }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("popup window never appeared");
    }

    private void Shot(string name)
    {
        try { Capture.Screen().ToFile(Path.Combine(_shots, name)); } catch { }
    }

    [SkippableFact]
    public async Task ClickingAllowResolvesThePermission()
    {
        Skip.If(_app is null, "APP_EXE not set");

        var pending = PostPermission("""
        {"hook_event_name":"PermissionRequest","session_id":"e2e","cwd":"C:\\work\\demo-project",
         "tool_name":"Bash","tool_input":{"command":"git push origin main","description":"Push the release"}}
        """);

        using var automation = new UIA3Automation();
        var popup = WaitForPopup(automation);
        Shot("popup-permission.png");

        var allow = popup.FindFirstDescendant(cf => cf.ByName("Allow"))?.AsButton();
        Assert.NotNull(allow);
        allow!.Invoke();

        var response = await pending;
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"behavior\":\"allow\"", body);
        Assert.Contains("git push origin main", body);
    }

    [SkippableFact]
    public async Task ClickingDenyBlocksWithMessage()
    {
        Skip.If(_app is null, "APP_EXE not set");

        var pending = PostPermission("""
        {"hook_event_name":"PermissionRequest","session_id":"e2e","cwd":"C:\\work\\demo-project",
         "tool_name":"Edit","tool_input":{"file_path":"C:\\work\\demo-project\\main.cs",
         "old_string":"var retries = 3;","new_string":"var retries = 5;"}}
        """);

        using var automation = new UIA3Automation();
        var popup = WaitForPopup(automation);
        Shot("popup-diff.png");

        var deny = popup.FindFirstDescendant(cf => cf.ByName("Deny"))?.AsButton();
        Assert.NotNull(deny);
        deny!.Invoke();

        var body = await (await pending).Content.ReadAsStringAsync();
        Assert.Contains("\"behavior\":\"deny\"", body);
        Assert.Contains("Denied via Claude Approvals", body);
    }

    [SkippableFact]
    public async Task QuestionFormSubmitsChosenOption()
    {
        Skip.If(_app is null, "APP_EXE not set");

        var pending = PostPermission("""
        {"hook_event_name":"PermissionRequest","session_id":"e2e","cwd":"C:\\work\\demo-project",
         "tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Tabs or spaces?",
         "header":"Style","multiSelect":false,"options":[
           {"label":"Tabs","description":"use tabs"},
           {"label":"Spaces","description":"use spaces"}]}]}}
        """);

        using var automation = new UIA3Automation();
        var popup = WaitForPopup(automation);
        Shot("popup-question.png");

        // Options render as "Label - Description" radio rows.
        var spaces = popup.FindFirstDescendant(cf => cf.ByName("Spaces - use spaces"))?.AsRadioButton();
        Assert.NotNull(spaces);
        spaces!.Click();
        var send = popup.FindFirstDescendant(cf => cf.ByName("Send"))?.AsButton();
        Assert.NotNull(send);
        send!.Invoke();

        var body = await (await pending).Content.ReadAsStringAsync();
        Assert.Contains("\"behavior\":\"allow\"", body);
        // De-space both sides for a whitespace-robust comparison.
        Assert.Contains("\"Tabsorspaces?\":\"Spaces\"", body.Replace(" ", ""));
        Shot("after-question.png");
    }

    /// <summary>
    /// The REAL integration: pipe a payload into hooks/permission.ps1 exactly as
    /// Claude Code does, click Allow on the popup it causes, and assert the
    /// script prints the decision JSON on stdout.
    /// </summary>
    [SkippableFact]
    public async Task HookScriptRoundTripsThroughTheUi()
    {
        Skip.If(_app is null, "APP_EXE not set");
        var hook = Environment.GetEnvironmentVariable("HOOK_PS1");
        Skip.If(hook is null || !File.Exists(hook), "HOOK_PS1 not set");

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{hook}\"")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        psi.Environment["CLAUDE_APPROVALS_PORT"] = Port.ToString();
        // Token: the script reads %APPDATA%\ClaudeApprovals\token; write it there.
        var tokenDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeApprovals");
        Directory.CreateDirectory(tokenDir);
        await File.WriteAllTextAsync(Path.Combine(tokenDir, "token"), Token);

        var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteAsync("""
        {"hook_event_name":"PermissionRequest","session_id":"e2e-hook","cwd":"C:\\work\\demo-project",
         "tool_name":"Bash","tool_input":{"command":"echo from-hook-script"}}
        """);
        proc.StandardInput.Close();

        using var automation = new UIA3Automation();
        var popup = WaitForPopup(automation);
        Shot("popup-from-hook.png");
        popup.FindFirstDescendant(cf => cf.ByName("Allow"))!.AsButton()!.Invoke();

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("\"behavior\":\"allow\"", stdout);
        Assert.Contains("echo from-hook-script", stdout);
    }
}
