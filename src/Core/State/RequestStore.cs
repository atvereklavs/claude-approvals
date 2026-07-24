using ClaudeApprovals.Core.Models;

namespace ClaudeApprovals.Core.State;

/// <summary>One in-flight permission request awaiting a decision.</summary>
public sealed class PendingRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public required HookPayload Payload { get; init; }
    public required ToolSummary Summary { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    private Action<string?>? _respond;

    public PendingRequest() { }

    internal void AttachResponder(Action<string?> respond) => _respond = respond;

    /// <summary>Fulfil the parked HTTP response exactly once; later calls no-op.</summary>
    public void Fulfil(string? body)
    {
        var r = Interlocked.Exchange(ref _respond, null);
        r?.Invoke(body);
    }
}

/// <summary>
/// The arbiter (port of the Mac RequestStore): FIFO queue of pending requests,
/// idempotent first-wins resolve, per-request answer timeout, client-drop
/// removal, session + project auto-allow short-circuit. Thread-safe; UI layers
/// subscribe to the events and marshal to their own thread.
/// </summary>
public sealed class RequestStore : IDisposable
{
    private readonly List<PendingRequest> _pending = new();
    private readonly Dictionary<Guid, Timer> _timeouts = new();
    private readonly object _lock = new();
    private readonly TimeSpan _answerTimeout;

    public SessionRuleStore? SessionRules { get; set; }
    public ProjectRuleStore? ProjectRules { get; set; }

    /// <summary>Manual privacy pause: everything falls through to the terminal.</summary>
    public volatile bool Paused;
    /// <summary>Auto-pause (mic in use). Independent of the manual flag so a call
    /// ending never clears a deliberate pause.</summary>
    public volatile bool AutoPaused;

    public bool EffectivePaused => Paused || AutoPaused;

    public event Action<PendingRequest>? OnEnqueue;
    public event Action<PendingRequest, Decision?, DecisionSource>? OnResolve;
    /// <summary>Every terminal outcome, incl. auto-allow + drops (audit hook).</summary>
    public event Action<PendingRequest, Decision?, DecisionSource>? OnOutcome;

    public RequestStore(TimeSpan? answerTimeout = null)
    {
        _answerTimeout = answerTimeout ?? TimeSpan.FromSeconds(585);
    }

    public IReadOnlyList<PendingRequest> Pending { get { lock (_lock) return _pending.ToList(); } }
    public PendingRequest? Front { get { lock (_lock) return _pending.FirstOrDefault(); } }
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>
    /// Register a request. Auto-allow rules short-circuit (never enqueued;
    /// returns null). Otherwise returns the id and fires OnEnqueue.
    /// </summary>
    public Guid? Enqueue(HookPayload payload, Action<string?> respond)
    {
        var request = new PendingRequest { Payload = payload, Summary = ToolSummary.Make(payload) };
        request.AttachResponder(respond);

        // Privacy pause: hand the decision straight back to the terminal —
        // checked BEFORE auto-allow so nothing at all surfaces while paused.
        if (EffectivePaused)
        {
            request.Fulfil(null);
            OnOutcome?.Invoke(request, new Decision.NoOpinion(), DecisionSource.Paused);
            return null;
        }

        var sessionHit = SessionRules?.Matches(payload) ?? false;
        var projectHit = ProjectRules?.Matches(payload) ?? false;
        if (sessionHit || projectHit)
        {
            request.Fulfil(HookResponse.Body(new Decision.Allow(), payload));
            OnOutcome?.Invoke(request, new Decision.Allow(), DecisionSource.AutoAllow);
            return null;
        }

        lock (_lock)
        {
            _pending.Add(request);
            _timeouts[request.Id] = new Timer(
                _ => Resolve(request.Id, new Decision.NoOpinion(), DecisionSource.Timeout),
                null, _answerTimeout, Timeout.InfiniteTimeSpan);
        }
        OnEnqueue?.Invoke(request);
        return request.Id;
    }

    /// <summary>Idempotent first-wins resolve. Returns false if already settled.</summary>
    public bool Resolve(Guid id, Decision decision, DecisionSource source)
    {
        PendingRequest? request;
        lock (_lock)
        {
            var idx = _pending.FindIndex(r => r.Id == id);
            if (idx < 0) return false;
            request = _pending[idx];
            _pending.RemoveAt(idx);
            CancelTimeout(id);
        }

        if (decision is Decision.AllowForSession) SessionRules?.Remember(request.Payload);
        if (decision is Decision.AllowForProject) ProjectRules?.Remember(request.Payload);

        request.Fulfil(HookResponse.Body(decision, request.Payload));
        OnResolve?.Invoke(request, decision, source);
        OnOutcome?.Invoke(request, decision, source);
        return true;
    }

    /// <summary>The peer closed before answering: drop without responding.</summary>
    public void ClientDropped(Guid id)
    {
        PendingRequest? request;
        lock (_lock)
        {
            var idx = _pending.FindIndex(r => r.Id == id);
            if (idx < 0) return;
            request = _pending[idx];
            _pending.RemoveAt(idx);
            CancelTimeout(id);
        }
        OnResolve?.Invoke(request, null, DecisionSource.ClientDropped);
        OnOutcome?.Invoke(request, null, DecisionSource.ClientDropped);
    }

    public void SessionStopped(string sessionId) => SessionRules?.ClearSession(sessionId);

    private void CancelTimeout(Guid id)
    {
        if (_timeouts.Remove(id, out var t)) t.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var t in _timeouts.Values) t.Dispose();
            _timeouts.Clear();
        }
    }
}
