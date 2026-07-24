using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeApprovals.Core.Models;

namespace ClaudeApprovals.Core.State;

/// <summary>
/// Append-only audit trail: one JSON line per terminal outcome at
/// %APPDATA%\ClaudeApprovals\decisions.jsonl (override: CLAUDE_APPROVALS_LOG).
/// Fail-silent — auditing must never affect the approval path.
/// </summary>
public static class DecisionLog
{
    public static string LogPath =>
        Environment.GetEnvironmentVariable("CLAUDE_APPROVALS_LOG")
        ?? Path.Combine(ConfigDir.Path, "decisions.jsonl");

    /// <summary>Pure record builder (unit-testable).</summary>
    public static JsonObject Record(PendingRequest request, Decision? decision,
                                    DecisionSource source, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var rec = new JsonObject
        {
            ["ts"] = t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["session"] = request.Payload.SessionLabel,
            ["session_id"] = request.Payload.SessionId ?? "",
            ["tool"] = request.Payload.ToolName ?? "?",
            ["title"] = request.Summary.Title,
            ["detail"] = Truncate(request.Summary.Detail, 200),
            ["source"] = SourceName(source),
            ["latency_s"] = (int)Math.Round((t - request.ReceivedAt).TotalSeconds),
        };
        if (request.Payload.Cwd is not null) rec["cwd"] = request.Payload.Cwd;
        switch (decision)
        {
            case Decision.Allow: rec["decision"] = "allow"; break;
            case Decision.AllowForSession: rec["decision"] = "allow_for_session"; break;
            case Decision.AllowForProject: rec["decision"] = "allow_for_project"; break;
            case Decision.Deny d: rec["decision"] = "deny"; rec["reason"] = d.Reason; break;
            case Decision.Answer a:
                rec["decision"] = "answer";
                var answers = new JsonObject();
                foreach (var (q, v) in a.Answers) answers[q] = v;
                rec["answers"] = answers;
                break;
            case Decision.NoOpinion: rec["decision"] = "no_opinion"; break;
            case null: rec["decision"] = "dropped"; break;
        }
        return rec;
    }

    public static void Append(PendingRequest request, Decision? decision, DecisionSource source)
    {
        try
        {
            var rec = Record(request, decision, source);
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, rec.ToJsonString() + "\n");
        }
        catch { /* fail-silent */ }
    }

    private static string SourceName(DecisionSource s) => s switch
    {
        DecisionSource.Popup => "popup",
        DecisionSource.Hotkey => "hotkey",
        DecisionSource.AutoAllow => "autoAllow",
        DecisionSource.Timeout => "timeout",
        DecisionSource.ClientDropped => "clientDropped",
        _ => "unknown",
    };

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
