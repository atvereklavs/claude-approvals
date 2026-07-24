using System.Text.Json.Nodes;
using ClaudeApprovals.Core.Models;
using Xunit;

namespace ClaudeApprovals.Core.Tests;

public class ModelTests
{
    private static HookPayload P(string json) => HookPayload.Parse(json)!;

    private static JsonObject Obj(string? body) => (JsonObject)JsonNode.Parse(body!)!;

    // -- HookResponse schema: PermissionRequest ------------------------------

    [Fact]
    public void PermissionRequestAllowEchoesInput()
    {
        var p = P("""{"hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"git push","description":"d"}}""");
        var hso = (JsonObject)Obj(HookResponse.Body(new Decision.Allow(), p))["hookSpecificOutput"]!;
        Assert.Equal("PermissionRequest", (string)hso["hookEventName"]!);
        var decision = (JsonObject)hso["decision"]!;
        Assert.Equal("allow", (string)decision["behavior"]!);
        Assert.Equal("git push", (string)decision["updatedInput"]!["command"]!);
    }

    [Fact]
    public void PermissionRequestDenyCarriesMessage()
    {
        var p = P("""{"hook_event_name":"PermissionRequest","tool_name":"Bash"}""");
        var decision = (JsonObject)Obj(HookResponse.Body(new Decision.Deny("nope"), p))["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("deny", (string)decision["behavior"]!);
        Assert.Equal("nope", (string)decision["message"]!);
        Assert.False(decision.ContainsKey("updatedInput"));
    }

    [Fact]
    public void PreToolUseAllowUsesPermissionDecision()
    {
        var p = P("""{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ls"}}""");
        var hso = (JsonObject)Obj(HookResponse.Body(new Decision.Allow(), p))["hookSpecificOutput"]!;
        Assert.Equal("PreToolUse", (string)hso["hookEventName"]!);
        Assert.Equal("allow", (string)hso["permissionDecision"]!);
        Assert.False(hso.ContainsKey("decision"));
    }

    [Fact]
    public void NoOpinionIsEmpty()
    {
        var p = P("""{"hook_event_name":"PermissionRequest","tool_name":"Bash"}""");
        Assert.Null(HookResponse.Body(new Decision.NoOpinion(), p));
    }

    [Fact]
    public void AnswerInjectsAnswersAndPreservesQuestions()
    {
        var p = P("""{"hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Q?"}]}}""");
        var decision = (JsonObject)Obj(HookResponse.Body(
            new Decision.Answer(new Dictionary<string, string> { ["Q?"] = "Spaces" }), p))
            ["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", (string)decision["behavior"]!);
        var updated = (JsonObject)decision["updatedInput"]!;
        Assert.Equal("Spaces", (string)updated["answers"]!["Q?"]!);
        Assert.True(updated.ContainsKey("questions"));
    }

    // -- ToolSummary ---------------------------------------------------------

    [Fact]
    public void ToolSummaryBash()
    {
        var s = ToolSummary.Make(P("""{"tool_name":"Bash","tool_input":{"command":"npm test"}}"""));
        Assert.Equal("npm test", s.Detail);
        Assert.Equal("npm test", s.FullText);
    }

    [Fact]
    public void EditProducesDiff()
    {
        var s = ToolSummary.Make(P("""{"tool_name":"Edit","tool_input":{"file_path":"/a/main.swift","old_string":"let x = 1","new_string":"let x = 2"}}"""));
        Assert.Contains(s.Diff!, l => l.Kind == DiffKind.Removed && l.Text == "let x = 1");
        Assert.Contains(s.Diff!, l => l.Kind == DiffKind.Added && l.Text == "let x = 2");
        Assert.Contains("main.swift", s.Title);
    }

    [Fact]
    public void SessionLabelFromCwd()
    {
        Assert.Equal("ai-system", P("""{"cwd":"/Users/x/ai-system","session_id":"abcdef123"}""").SessionLabel);
    }

    [Fact]
    public void ExitPlanModeExtractsDecodedPlan()
    {
        var s = ToolSummary.Make(P("""{"tool_name":"ExitPlanMode","tool_input":{"plan":"# Title\n\n- step one\n- step two"}}"""));
        Assert.Equal("# Title\n\n- step one\n- step two", s.Plan);
        Assert.Equal("Implementation plan", s.Title);
    }

    // -- TextDiff ------------------------------------------------------------

    [Fact]
    public void DiffIsolatesChangedLine()
    {
        var d = TextDiff.Lines("a\nb\nc", "a\nB\nc");
        Assert.Equal(new[] { "b" }, d.Where(l => l.Kind == DiffKind.Removed).Select(l => l.Text));
        Assert.Equal(new[] { "B" }, d.Where(l => l.Kind == DiffKind.Added).Select(l => l.Text));
        Assert.Equal("+1 −1", TextDiff.Stat(d));
    }

    // -- AskContent ----------------------------------------------------------

    [Fact]
    public void AskParsesQuestionAndOptions()
    {
        var p = P("""{"tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Tabs or spaces?","header":"Style","multiSelect":false,"options":[{"label":"Tabs","description":"use tabs"},{"label":"Spaces","description":"use spaces"}]}]}}""");
        var s = ToolSummary.Make(p);
        Assert.NotNull(s.Ask);
        Assert.Equal("Tabs or spaces?", s.Ask!.First!.Text);
        Assert.Equal(new[] { "Tabs", "Spaces" }, s.Ask.First!.Options.Select(o => o.Label));
        Assert.False(s.Ask.First!.MultiSelect);
    }

    // -- Lossless tool_input round-trip --------------------------------------

    [Fact]
    public void UpdatedInputRoundTripsUnknownFields()
    {
        var p = P("""{"hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"x","n":3,"flag":true,"nested":{"a":[1,"x",null]}}}""");
        var updated = (JsonObject)Obj(HookResponse.Body(new Decision.Allow(), p))
            ["hookSpecificOutput"]!["decision"]!["updatedInput"]!;
        Assert.Equal(3, (int)updated["n"]!);
        Assert.True((bool)updated["flag"]!);
        Assert.Equal("x", (string)updated["nested"]!["a"]![1]!);
    }
}
