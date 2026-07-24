using ClaudeApprovals.Core.Models;

namespace ClaudeApprovals.Core.State;

/// <summary>Live state of a Claude Code session, inferred from its hook stream.</summary>
public enum SessionState
{
    WaitingApproval, // a permission decision is pending (rank 0 — most urgent)
    WaitingInput,    // idle_prompt — waiting for the user
    Working,         // processing (after a prompt, before Stop)
    Idle,            // finished responding
}

public sealed class SessionInfo
{
    public required string Id { get; init; }
    public required string Label { get; set; }
    public string? Cwd { get; set; }
    public SessionState BaseState { get; set; } = SessionState.Idle;
    public int Pending { get; set; }
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A pending approval always outranks the event-derived base state.</summary>
    public SessionState DisplayState => Pending > 0 ? SessionState.WaitingApproval : BaseState;

    public string StateLabel => DisplayState switch
    {
        SessionState.WaitingApproval => "needs approval",
        SessionState.WaitingInput => "waiting for you",
        SessionState.Working => "working",
        _ => "idle",
    };
}

/// <summary>
/// Tracks every known session and its state (port of the Mac SessionRegistry).
/// Driven by notify events (SessionStart / UserPromptSubmit / Stop / SessionEnd
/// / idle notifications) plus the permission queue. Thread-safe; Changed fires
/// on the caller's thread — UI marshals itself.
/// </summary>
public sealed class SessionRegistry
{
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private readonly object _lock = new();
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public event Action? Changed;

    public IReadOnlyList<SessionInfo> Ordered
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Values
                    .OrderBy(s => (int)s.DisplayState)
                    .ThenByDescending(s => s.LastActivity)
                    .ToList();
            }
        }
    }

    public int Count { get { lock (_lock) return _sessions.Count; } }

    /// <summary>Most urgent state across sessions, or null when empty.</summary>
    public SessionState? Aggregate
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count == 0 ? null : _sessions.Values.Min(s => s.DisplayState);
            }
        }
    }

    public Dictionary<string, int> StateCounts()
    {
        lock (_lock)
        {
            return _sessions.Values
                .GroupBy(s => s.DisplayState.ToString())
                .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    public void NoteEvent(HookPayload p)
    {
        if (p.SessionId is null) return;
        lock (_lock)
        {
            if (p.HookEventName == "SessionEnd")
            {
                _sessions.Remove(p.SessionId);
            }
            else
            {
                var s = Get(p);
                if (!string.IsNullOrEmpty(p.Cwd)) { s.Cwd = p.Cwd; s.Label = p.SessionLabel; }
                s.LastActivity = DateTimeOffset.UtcNow;
                s.BaseState = p.HookEventName switch
                {
                    "SessionStart" => SessionState.Idle,
                    "UserPromptSubmit" => SessionState.Working,
                    "Stop" => SessionState.Idle,
                    "Notification" when p.NotificationType == "idle_prompt" => SessionState.WaitingInput,
                    _ => s.BaseState,
                };
                Prune();
            }
        }
        Changed?.Invoke();
    }

    public void IncPending(HookPayload p)
    {
        if (p.SessionId is null) return;
        lock (_lock)
        {
            var s = Get(p);
            s.Pending++;
            s.LastActivity = DateTimeOffset.UtcNow;
        }
        Changed?.Invoke();
    }

    public void DecPending(HookPayload p)
    {
        if (p.SessionId is null) return;
        lock (_lock)
        {
            if (_sessions.TryGetValue(p.SessionId, out var s))
            {
                s.Pending = Math.Max(0, s.Pending - 1);
                s.LastActivity = DateTimeOffset.UtcNow;
            }
        }
        Changed?.Invoke();
    }

    private SessionInfo Get(HookPayload p)
    {
        if (_sessions.TryGetValue(p.SessionId!, out var s)) return s;
        var created = new SessionInfo { Id = p.SessionId!, Label = p.SessionLabel, Cwd = p.Cwd };
        _sessions[p.SessionId!] = created;
        return created;
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - StaleAfter;
        foreach (var key in _sessions.Where(kv =>
                kv.Value.Pending == 0 && kv.Value.DisplayState == SessionState.Idle
                && kv.Value.LastActivity < cutoff)
            .Select(kv => kv.Key).ToList())
        {
            _sessions.Remove(key);
        }
    }
}
