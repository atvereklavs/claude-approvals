using System.Diagnostics;
using System.Text;
using Xunit;

namespace ClaudeApprovals.App.E2E;

/// <summary>
/// Paused-instance E2E: launch the real app with CLAUDE_APPROVALS_PAUSED=1 and
/// prove a permission request falls straight through — empty 200 within a
/// couple of seconds, no popup involved, decision logged as paused.
/// </summary>
public class PausedE2ETests : IDisposable
{
    private const int Port = 18797;
    private const string Token = "e2e-paused";
    private readonly Process? _app;
    private readonly string _shots;

    public PausedE2ETests()
    {
        _shots = Environment.GetEnvironmentVariable("E2E_SHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(_shots);
        var exe = Environment.GetEnvironmentVariable("APP_EXE");
        if (exe is null || !File.Exists(exe)) return;

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["CLAUDE_APPROVALS_PORT"] = Port.ToString();
        psi.Environment["CLAUDE_APPROVALS_TOKEN"] = Token;
        psi.Environment["CLAUDE_APPROVALS_PAUSED"] = "1";
        psi.Environment["CLAUDE_APPROVALS_AUTOPAUSE"] = "0";
        psi.Environment["CLAUDE_APPROVALS_RULES"] = Path.Combine(_shots, "paused-rules.json");
        psi.Environment["CLAUDE_APPROVALS_LOG"] = Path.Combine(_shots, "paused-decisions.jsonl");
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

    [SkippableFact]
    public async Task PausedInstanceFallsThroughToTerminal()
    {
        Skip.If(_app is null, "APP_EXE not set");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/v1/permission")
        {
            Content = new StringContent(
                """{"hook_event_name":"PermissionRequest","session_id":"p1","cwd":"C:\\work\\demo","tool_name":"Bash","tool_input":{"command":"secret deploy"}}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Notch-Token", Token);

        var sw = Stopwatch.StartNew();
        var resp = await http.SendAsync(req);
        sw.Stop();

        Assert.Equal(200, (int)resp.StatusCode);
        Assert.Equal("", await resp.Content.ReadAsStringAsync()); // empty → terminal prompt
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "paused fall-through must be immediate");

        // Audit trail records the paused fall-through.
        var logPath = Path.Combine(_shots, "paused-decisions.jsonl");
        for (var i = 0; i < 20 && !File.Exists(logPath); i++) Thread.Sleep(100);
        var log = await File.ReadAllTextAsync(logPath);
        Assert.Contains("\"source\":\"paused\"", log.Replace(" ", ""));
    }
}
