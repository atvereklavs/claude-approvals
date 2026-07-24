using System.Diagnostics;
using Xunit;

namespace ClaudeApprovals.App.E2E;

/// <summary>
/// Single-instance guard: a second app started on the same port must exit 0
/// within seconds (healthy sibling wins) while the first keeps serving.
/// </summary>
public class SingleInstanceE2ETests : IDisposable
{
    private const int Port = 18799;
    private const string Token = "e2e-single";
    private readonly Process? _app;
    private readonly string _shots;

    public SingleInstanceE2ETests()
    {
        _shots = Environment.GetEnvironmentVariable("E2E_SHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(_shots);
        var exe = Environment.GetEnvironmentVariable("APP_EXE");
        if (exe is null || !File.Exists(exe)) return;
        _app = Start(exe);

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

    private Process Start(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.Environment["CLAUDE_APPROVALS_PORT"] = Port.ToString();
        psi.Environment["CLAUDE_APPROVALS_TOKEN"] = Token;
        psi.Environment["CLAUDE_APPROVALS_AUTOPAUSE"] = "0";
        psi.Environment["CLAUDE_APPROVALS_RULES"] = Path.Combine(_shots, "single-rules.json");
        psi.Environment["CLAUDE_APPROVALS_LOG"] = Path.Combine(_shots, "single-decisions.jsonl");
        return Process.Start(psi)!;
    }

    public void Dispose()
    {
        try { _app?.Kill(entireProcessTree: true); } catch { }
    }

    [SkippableFact]
    public async Task DuplicateInstanceExitsCleanlyAndSiblingSurvives()
    {
        Skip.If(_app is null, "APP_EXE not set");
        var exe = Environment.GetEnvironmentVariable("APP_EXE")!;

        var duplicate = Start(exe);
        var exited = duplicate.WaitForExit(15_000);
        Assert.True(exited, "duplicate instance must exit, not linger as a zombie");
        Assert.Equal(0, duplicate.ExitCode);

        // The original instance is untouched.
        using var http = new HttpClient();
        var health = await http.GetStringAsync($"http://127.0.0.1:{Port}/v1/health");
        Assert.Contains("\"ok\":true", health);
    }
}
