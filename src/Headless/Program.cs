using ClaudeApprovals.Core.Models;
using ClaudeApprovals.Core.Server;
using ClaudeApprovals.Core.State;

// Headless host: runs the full arbiter + HTTP server without any UI. Used for
// local development on non-Windows machines and for protocol smoke tests.
//   CLAUDE_APPROVALS_PORT   listen port (default 8790)
//   CLAUDE_APPROVALS_TOKEN  shared token (default: none → no auth)
//   CLAUDE_APPROVALS_AUTO   allow | deny | noop → auto-resolve (else: print & park)

var port = int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_APPROVALS_PORT"), out var p) ? p : 8790;
var token = Environment.GetEnvironmentVariable("CLAUDE_APPROVALS_TOKEN");
var auto = Environment.GetEnvironmentVariable("CLAUDE_APPROVALS_AUTO");

using var store = new RequestStore();
store.SessionRules = new SessionRuleStore();
store.ProjectRules = new ProjectRuleStore();
store.OnOutcome += (req, decision, source) => DecisionLog.Append(req, decision, source);

store.OnEnqueue += req =>
{
    Console.WriteLine($"[pending] {req.Payload.SessionLabel} · {req.Summary.Title} · {req.Summary.Detail}");
    Decision? decision = auto switch
    {
        "allow" => new Decision.Allow(),
        "deny" => new Decision.Deny("Denied via Claude Approvals (auto)"),
        "noop" => new Decision.NoOpinion(),
        _ => null,
    };
    if (decision is not null)
        Task.Delay(50).ContinueWith(_ => store.Resolve(req.Id, decision, DecisionSource.Popup));
};

using var server = new ApprovalServer(store, port, token);
server.OnNotify += payload =>
    Console.WriteLine($"[notify] {payload.HookEventName}/{payload.NotificationType} {payload.SessionLabel}");

Console.WriteLine($"claude-approvals headless on http://127.0.0.1:{server.Port} (auto={auto ?? "off"})");
await Task.Delay(Timeout.InfiniteTimeSpan);
