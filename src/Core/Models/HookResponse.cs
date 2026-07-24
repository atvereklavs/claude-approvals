using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeApprovals.Core.Models;

/// <summary>
/// Builds the exact stdout JSON Claude Code expects from a hook. Wire format
/// verified against the CLI's own consumer (see the Mac app's README →
/// "Verified hook facts"); this port must stay byte-compatible.
/// </summary>
public static class HookResponse
{
    /// <summary>
    /// The response body for a decision, or null for "no opinion" (empty body →
    /// Claude shows its own prompt).
    /// </summary>
    public static string? Body(Decision decision, HookPayload payload)
    {
        var eventName = payload.HookEventName ?? "PermissionRequest";
        return decision switch
        {
            Decision.NoOpinion => null,
            Decision.Allow or Decision.AllowForSession or Decision.AllowForProject =>
                AllowBody(eventName, payload.ToolInput?.DeepClone()),
            Decision.Answer a => AllowBody(eventName, MergeAnswers(payload.ToolInput, a.Answers)),
            Decision.Deny d => DenyBody(eventName, d.Reason),
            _ => null,
        };
    }

    private static string AllowBody(string eventName, JsonNode? toolInput)
    {
        if (eventName == "PreToolUse")
        {
            return Serialize(new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "allow",
                    ["permissionDecisionReason"] = "Approved via Claude Approvals",
                },
            });
        }
        var decision = new JsonObject { ["behavior"] = "allow" };
        if (toolInput is not null) decision["updatedInput"] = toolInput;
        return Serialize(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = decision,
            },
        });
    }

    private static string DenyBody(string eventName, string reason)
    {
        if (eventName == "PreToolUse")
        {
            return Serialize(new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = reason,
                },
            });
        }
        return Serialize(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = new JsonObject
                {
                    ["behavior"] = "deny",
                    ["message"] = reason,
                },
            },
        });
    }

    /// <summary>Merge answers (question text → label) into tool_input.answers.</summary>
    private static JsonNode MergeAnswers(JsonNode? input, IReadOnlyDictionary<string, string> answers)
    {
        var obj = input?.DeepClone() as JsonObject ?? new JsonObject();
        var answerMap = obj["answers"] as JsonObject ?? new JsonObject();
        obj.Remove("answers");
        foreach (var (q, a) in answers) answerMap[q] = a;
        obj["answers"] = answerMap;
        return obj;
    }

    private static string Serialize(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
