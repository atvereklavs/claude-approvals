using System.Text.Json.Nodes;

namespace ClaudeApprovals.Core.Models;

/// <summary>
/// The JSON Claude Code sends on stdin for a hook, forwarded verbatim by the
/// hook script into POST /v1/permission or /v1/notify. Only the fields we use
/// are read; <see cref="ToolInput"/> stays a raw JsonNode so an "allow" can
/// echo it back byte-faithfully as updatedInput.
/// </summary>
public sealed class HookPayload
{
    public string? HookEventName { get; init; }
    public string? SessionId { get; init; }
    public string? Cwd { get; init; }
    public string? PermissionMode { get; init; }
    public string? TranscriptPath { get; init; }
    public string? ToolName { get; init; }
    public JsonNode? ToolInput { get; init; }
    public string? NotificationType { get; init; }
    public string? Message { get; init; }

    public static HookPayload? Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return null; }
        if (root is not JsonObject obj) return null;

        return new HookPayload
        {
            HookEventName = Str(obj, "hook_event_name"),
            SessionId = Str(obj, "session_id"),
            Cwd = Str(obj, "cwd"),
            PermissionMode = Str(obj, "permission_mode"),
            TranscriptPath = Str(obj, "transcript_path"),
            ToolName = Str(obj, "tool_name"),
            ToolInput = obj["tool_input"],
            NotificationType = Str(obj, "notification_type"),
            Message = Str(obj, "message"),
        };
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Short human label: cwd basename, else truncated session id.</summary>
    public string SessionLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(Cwd))
            {
                var basename = Path.GetFileName(Cwd.TrimEnd('/', '\\'));
                if (!string.IsNullOrEmpty(basename)) return basename;
            }
            if (!string.IsNullOrEmpty(SessionId))
                return SessionId.Length > 8 ? SessionId[..8] : SessionId;
            return "session";
        }
    }

    /// <summary>String field inside tool_input, or null.</summary>
    public string? InputString(string key) =>
        ToolInput is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
