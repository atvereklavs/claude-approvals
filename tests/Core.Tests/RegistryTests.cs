using ClaudeApprovals.Core.Models;
using ClaudeApprovals.Core.State;
using Xunit;

namespace ClaudeApprovals.Core.Tests;

public class RegistryTests
{
    private static HookPayload P(string json) => HookPayload.Parse(json)!;

    [Fact]
    public void StateMachineStartWorkApprovalWorkIdleEnd()
    {
        var reg = new SessionRegistry();
        reg.NoteEvent(P("""{"hook_event_name":"SessionStart","session_id":"s","cwd":"/x/proj"}"""));
        reg.NoteEvent(P("""{"hook_event_name":"UserPromptSubmit","session_id":"s","cwd":"/x/proj"}"""));
        Assert.Equal(SessionState.Working, reg.Ordered[0].DisplayState);
        Assert.Equal("proj", reg.Ordered[0].Label);

        reg.IncPending(P("""{"session_id":"s","tool_name":"Bash"}"""));
        Assert.Equal(SessionState.WaitingApproval, reg.Ordered[0].DisplayState);
        Assert.Equal(SessionState.WaitingApproval, reg.Aggregate);

        reg.DecPending(P("""{"session_id":"s","tool_name":"Bash"}"""));
        Assert.Equal(SessionState.Working, reg.Ordered[0].DisplayState);

        reg.NoteEvent(P("""{"hook_event_name":"Stop","session_id":"s"}"""));
        Assert.Equal(SessionState.Idle, reg.Ordered[0].DisplayState);

        reg.NoteEvent(P("""{"hook_event_name":"SessionEnd","session_id":"s"}"""));
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void OrderingIsUrgencyThenRecency()
    {
        var reg = new SessionRegistry();
        reg.NoteEvent(P("""{"hook_event_name":"UserPromptSubmit","session_id":"working","cwd":"/w"}"""));
        reg.NoteEvent(P("""{"hook_event_name":"Notification","notification_type":"idle_prompt","session_id":"waiting","cwd":"/i"}"""));
        reg.NoteEvent(P("""{"hook_event_name":"Stop","session_id":"idle","cwd":"/d"}"""));
        reg.IncPending(P("""{"session_id":"approval","cwd":"/a"}"""));

        var order = reg.Ordered.Select(s => s.Id).ToArray();
        Assert.Equal(new[] { "approval", "waiting", "working", "idle" }, order);
    }

    [Fact]
    public void IdlePromptSetsWaitingInputAndStateCounts()
    {
        var reg = new SessionRegistry();
        reg.NoteEvent(P("""{"hook_event_name":"Notification","notification_type":"idle_prompt","session_id":"a","cwd":"/a"}"""));
        reg.NoteEvent(P("""{"hook_event_name":"UserPromptSubmit","session_id":"b","cwd":"/b"}"""));
        var counts = reg.StateCounts();
        Assert.Equal(1, counts["WaitingInput"]);
        Assert.Equal(1, counts["Working"]);
    }

    [Fact]
    public void ChangedFiresOnEvents()
    {
        var reg = new SessionRegistry();
        var fired = 0;
        reg.Changed += () => fired++;
        reg.NoteEvent(P("""{"hook_event_name":"SessionStart","session_id":"s","cwd":"/x"}"""));
        reg.IncPending(P("""{"session_id":"s"}"""));
        reg.DecPending(P("""{"session_id":"s"}"""));
        Assert.Equal(3, fired);
    }
}
