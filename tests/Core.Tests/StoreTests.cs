using System.Text.Json.Nodes;
using ClaudeApprovals.Core.Models;
using ClaudeApprovals.Core.State;
using Xunit;

namespace ClaudeApprovals.Core.Tests;

public class StoreTests
{
    private static HookPayload P(string json) => HookPayload.Parse(json)!;

    [Fact]
    public void FirstWinsResolveIsIdempotent()
    {
        using var store = new RequestStore(TimeSpan.FromMinutes(10));
        var bodies = new List<string?>();
        var id = store.Enqueue(P("""{"tool_name":"Bash","session_id":"s"}"""), b => bodies.Add(b))!.Value;

        Assert.Equal(1, store.PendingCount);
        Assert.True(store.Resolve(id, new Decision.Allow(), DecisionSource.Popup));
        Assert.False(store.Resolve(id, new Decision.Deny("x"), DecisionSource.Hotkey));
        Assert.Equal(0, store.PendingCount);
        Assert.Single(bodies); // parked response written exactly once
    }

    [Fact]
    public void QueueIsFifoAndRoutesToRightSession()
    {
        using var store = new RequestStore(TimeSpan.FromMinutes(10));
        string? aBody = null, bBody = null;
        var aCalled = false; var bCalled = false;
        var idA = store.Enqueue(P("""{"tool_name":"Bash","session_id":"A","cwd":"/A"}"""), b => { aBody = b; aCalled = true; })!.Value;
        store.Enqueue(P("""{"tool_name":"Bash","session_id":"B","cwd":"/B"}"""), b => { bBody = b; bCalled = true; });

        Assert.Equal(idA, store.Front!.Id);
        store.Resolve(idA, new Decision.Allow(), DecisionSource.Popup);
        Assert.True(aCalled);
        Assert.False(bCalled);
        Assert.Equal("B", store.Front!.Payload.SessionId);
        _ = aBody; _ = bBody;
    }

    [Fact]
    public void ClientDroppedRemovesWithoutResponding()
    {
        using var store = new RequestStore(TimeSpan.FromMinutes(10));
        var called = false;
        var id = store.Enqueue(P("""{"tool_name":"Bash","session_id":"s"}"""), _ => called = true)!.Value;
        store.ClientDropped(id);
        Assert.Equal(0, store.PendingCount);
        Assert.False(called);
    }

    [Fact]
    public void SessionAutoAllowShortCircuits()
    {
        using var store = new RequestStore(TimeSpan.FromMinutes(10));
        var rules = new SessionRuleStore();
        store.SessionRules = rules;
        rules.Remember(P("""{"tool_name":"Bash","session_id":"s","tool_input":{"command":"git status"}}"""));

        string? body = null;
        var id = store.Enqueue(P("""{"tool_name":"Bash","session_id":"s","tool_input":{"command":"git push"}}"""), b => body = b);
        Assert.Null(id); // never enqueued
        Assert.Equal(0, store.PendingCount);
        var decision = (JsonObject)JsonNode.Parse(body!)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", (string)decision["behavior"]!);
    }

    [Fact]
    public void SessionRulesAreSessionScopedAndClearable()
    {
        var rules = new SessionRuleStore();
        rules.Remember(P("""{"tool_name":"Bash","session_id":"s1","tool_input":{"command":"ls"}}"""));
        Assert.True(rules.Matches(P("""{"tool_name":"Bash","session_id":"s1","tool_input":{"command":"ls -la"}}""")));
        Assert.False(rules.Matches(P("""{"tool_name":"Bash","session_id":"s2","tool_input":{"command":"ls"}}""")));
        rules.ClearSession("s1");
        Assert.False(rules.Matches(P("""{"tool_name":"Bash","session_id":"s1","tool_input":{"command":"ls"}}""")));
    }

    [Fact]
    public void ProjectRulesAreScopedPersistentAndShortCircuit()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ca-rules-{Guid.NewGuid():N}.json");
        try
        {
            var a = new ProjectRuleStore(tmp);
            a.Remember(P("""{"cwd":"/x/projA","tool_name":"Bash","tool_input":{"command":"git push origin"}}"""));
            Assert.True(a.Matches(P("""{"cwd":"/x/projA","tool_name":"Bash","tool_input":{"command":"git pull"}}""")));
            Assert.False(a.Matches(P("""{"cwd":"/x/projB","tool_name":"Bash","tool_input":{"command":"git push"}}""")));

            // fresh instance → persisted
            var b = new ProjectRuleStore(tmp);
            Assert.True(b.Matches(P("""{"cwd":"/x/projA","tool_name":"Bash","tool_input":{"command":"git status"}}""")));

            using var store = new RequestStore(TimeSpan.FromMinutes(10));
            store.ProjectRules = b;
            string? body = null;
            var id = store.Enqueue(P("""{"hook_event_name":"PermissionRequest","session_id":"s9","cwd":"/x/projA","tool_name":"Bash","tool_input":{"command":"git fetch"}}"""), x => body = x);
            Assert.Null(id);
            Assert.NotNull(body);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void TimeoutResolvesNoOpinion()
    {
        using var store = new RequestStore(TimeSpan.FromMilliseconds(80));
        string? body = "sentinel";
        var settled = new ManualResetEventSlim();
        DecisionSource? source = null;
        store.OnOutcome += (_, _, s) => { source = s; settled.Set(); };
        store.Enqueue(P("""{"tool_name":"Bash","session_id":"s"}"""), b => body = b);
        Assert.True(settled.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(body); // no-opinion → empty
        Assert.Equal(DecisionSource.Timeout, source);
    }

    [Fact]
    public void DecisionLogRecordFormat()
    {
        var p = P("""{"session_id":"s1","cwd":"/x/proj","tool_name":"Bash","tool_input":{"command":"git push"}}""");
        var req = new PendingRequest
        {
            Payload = p, Summary = ToolSummary.Make(p),
            ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-7),
        };
        var allow = DecisionLog.Record(req, new Decision.Allow(), DecisionSource.Popup);
        var drop = DecisionLog.Record(req, null, DecisionSource.ClientDropped);
        var answer = DecisionLog.Record(req,
            new Decision.Answer(new Dictionary<string, string> { ["Q?"] = "Tabs" }), DecisionSource.Popup);

        Assert.Equal("allow", (string)allow["decision"]!);
        Assert.Equal("popup", (string)allow["source"]!);
        Assert.Equal("proj", (string)allow["session"]!);
        Assert.True((int)allow["latency_s"]! >= 6);
        Assert.Equal("dropped", (string)drop["decision"]!);
        Assert.Equal("Tabs", (string)answer["answers"]!["Q?"]!);
    }
}
