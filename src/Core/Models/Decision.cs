namespace ClaudeApprovals.Core.Models;

/// <summary>Where a decision came from (display + audit attribution).</summary>
public enum DecisionSource
{
    Popup,
    Hotkey,
    AutoAllow,
    Timeout,
    ClientDropped,
    Paused,
}

/// <summary>
/// A resolved verdict on a pending permission request. Closed hierarchy —
/// mirrors the Mac app's Decision enum.
/// </summary>
public abstract record Decision
{
    private Decision() { }

    /// <summary>Approve once.</summary>
    public sealed record Allow : Decision;

    /// <summary>Approve and remember an auto-allow rule for this session.</summary>
    public sealed record AllowForSession : Decision;

    /// <summary>Approve and persist an always-allow rule for this project.</summary>
    public sealed record AllowForProject : Decision;

    /// <summary>Deny with a message shown to Claude.</summary>
    public sealed record Deny(string Reason) : Decision;

    /// <summary>Answer an AskUserQuestion (question text → chosen answer).</summary>
    public sealed record Answer(IReadOnlyDictionary<string, string> Answers) : Decision;

    /// <summary>No opinion — empty response body; Claude falls back to its own prompt.</summary>
    public sealed record NoOpinion : Decision;
}
