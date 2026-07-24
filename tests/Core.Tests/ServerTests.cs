using System.Net.Sockets;
using System.Text;
using ClaudeApprovals.Core.Models;
using ClaudeApprovals.Core.Server;
using ClaudeApprovals.Core.State;
using Xunit;

namespace ClaudeApprovals.Core.Tests;

/// <summary>
/// Protocol tests against a live ApprovalServer on an ephemeral port — the
/// equivalent of the Mac's smoke-test.sh (health / allow+updatedInput echo /
/// 403 / 400 / client-drop).
/// </summary>
public class ServerTests : IDisposable
{
    private readonly RequestStore _store = new(TimeSpan.FromMinutes(10));
    private readonly ApprovalServer _server;
    private const string Token = "test-token";

    public ServerTests()
    {
        _server = new ApprovalServer(_store, port: 0, token: Token);
    }

    public void Dispose()
    {
        _server.Dispose();
        _store.Dispose();
    }

    private async Task<(int Status, string Body)> Post(string path, string body, string? token = null)
    {
        using var http = new HttpClient();
        var req = new System.Net.Http.HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{_server.Port}{path}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null) req.Headers.Add("X-Notch-Token", token);
        var resp = await http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthOk()
    {
        using var http = new HttpClient();
        var body = await http.GetStringAsync($"http://127.0.0.1:{_server.Port}/v1/health");
        Assert.Contains("\"ok\":true", body);
    }

    [Fact]
    public async Task PermissionAllowEchoesUpdatedInput()
    {
        _store.OnEnqueue += r => _store.Resolve(r.Id, new Decision.Allow(), DecisionSource.Popup);
        var (status, body) = await Post("/v1/permission",
            """{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"Bash","tool_input":{"command":"echo hi"}}""",
            Token);
        Assert.Equal(200, status);
        Assert.Contains("\"behavior\":\"allow\"", body);
        Assert.Contains("\"command\":\"echo hi\"", body);
    }

    [Fact]
    public async Task NoOpinionReturnsEmptyBody()
    {
        _store.OnEnqueue += r => _store.Resolve(r.Id, new Decision.NoOpinion(), DecisionSource.Popup);
        var (status, body) = await Post("/v1/permission",
            """{"hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{}}""", Token);
        Assert.Equal(200, status);
        Assert.Equal("", body);
    }

    [Fact]
    public async Task MissingTokenIs403()
    {
        var (status, _) = await Post("/v1/permission", """{"tool_name":"Bash"}""");
        Assert.Equal(403, status);
    }

    [Fact]
    public async Task GarbageBodyIs400()
    {
        var (status, _) = await Post("/v1/permission", "not json", Token);
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task NotifyStopClearsSessionRules()
    {
        var rules = new SessionRuleStore();
        _store.SessionRules = rules;
        rules.Remember(HookPayload.Parse("""{"tool_name":"Bash","session_id":"sX","tool_input":{"command":"ls"}}""")!);
        Assert.True(rules.Matches(HookPayload.Parse("""{"tool_name":"Bash","session_id":"sX","tool_input":{"command":"ls"}}""")!));

        var (status, _) = await Post("/v1/notify",
            """{"hook_event_name":"Stop","session_id":"sX"}""", Token);
        Assert.Equal(200, status);
        // Notify is handled synchronously before the response is written.
        Assert.False(rules.Matches(HookPayload.Parse("""{"tool_name":"Bash","session_id":"sX","tool_input":{"command":"ls"}}""")!));
    }

    [Fact]
    public async Task ClientDropRemovesPendingRequest()
    {
        var enqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropped = new TaskCompletionSource<DecisionSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.OnEnqueue += _ => enqueued.TrySetResult();
        _store.OnOutcome += (_, _, s) => dropped.TrySetResult(s);

        // Raw socket: send the request, then slam the connection shut while parked.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", _server.Port);
            var body = """{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"Bash","tool_input":{"command":"x"}}""";
            var request = $"POST /v1/permission HTTP/1.1\r\nHost: x\r\nX-Notch-Token: {Token}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes(request));
            await enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, _store.PendingCount);
        } // dispose → FIN

        var source = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DecisionSource.ClientDropped, source);
        Assert.Equal(0, _store.PendingCount);
    }
}
