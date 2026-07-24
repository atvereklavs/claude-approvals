using System.Text.Json;
using ClaudeApprovals.Core.Models;

namespace ClaudeApprovals.Core.State;

/// <summary>Shared match-key derivation for allow rules (port of RulePattern).</summary>
public static class RulePattern
{
    public static string Derive(HookPayload p) => p.ToolName switch
    {
        "Bash" => (p.InputString("command") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .FirstOrDefault() ?? "",
        "Edit" or "MultiEdit" or "Write" or "Read" => p.InputString("file_path") ?? "",
        _ => p.ToolName ?? "",
    };
}

/// <summary>In-memory "allow for this session" rules; cleared on session Stop.</summary>
public sealed class SessionRuleStore
{
    private readonly HashSet<(string Session, string Tool, string Pattern)> _rules = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _rules.Count; } }

    public void Remember(HookPayload p)
    {
        if (p.SessionId is null || p.ToolName is null) return;
        lock (_lock) _rules.Add((p.SessionId, p.ToolName, RulePattern.Derive(p)));
    }

    public bool Matches(HookPayload p)
    {
        if (p.SessionId is null || p.ToolName is null) return false;
        lock (_lock) return _rules.Contains((p.SessionId, p.ToolName, RulePattern.Derive(p)));
    }

    public void ClearSession(string sessionId)
    {
        lock (_lock) _rules.RemoveWhere(r => r.Session == sessionId);
    }

    public void ClearAll() { lock (_lock) _rules.Clear(); }
}

/// <summary>
/// Persistent "always allow in this project" rules, scoped to the exact cwd.
/// JSON at %APPDATA%\ClaudeApprovals\rules.json (override: CLAUDE_APPROVALS_RULES).
/// Save failures are swallowed — rules are convenience, not a gate.
/// </summary>
public sealed class ProjectRuleStore
{
    public sealed record Rule(string Cwd, string Tool, string Pattern);

    private readonly HashSet<Rule> _rules = new();
    private readonly object _lock = new();
    private readonly string _path;

    public ProjectRuleStore(string? path = null)
    {
        _path = path
            ?? Environment.GetEnvironmentVariable("CLAUDE_APPROVALS_RULES")
            ?? Path.Combine(ConfigDir.Path, "rules.json");
        Load();
    }

    public int Count { get { lock (_lock) return _rules.Count; } }

    public void Remember(HookPayload p)
    {
        if (string.IsNullOrEmpty(p.Cwd) || p.ToolName is null) return;
        lock (_lock)
        {
            _rules.Add(new Rule(p.Cwd, p.ToolName, RulePattern.Derive(p)));
            Save();
        }
    }

    public bool Matches(HookPayload p)
    {
        if (p.Cwd is null || p.ToolName is null) return false;
        lock (_lock) return _rules.Contains(new Rule(p.Cwd, p.ToolName, RulePattern.Derive(p)));
    }

    public void ClearAll() { lock (_lock) { _rules.Clear(); Save(); } }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var rules = JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(_path));
            if (rules is null) return;
            foreach (var r in rules) _rules.Add(r);
        }
        catch { /* fail-silent */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var sorted = _rules.OrderBy(r => (r.Cwd, r.Tool, r.Pattern)).ToList();
            var json = JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path + ".tmp", json);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch { /* fail-silent */ }
    }
}

/// <summary>Config directory: %APPDATA%\ClaudeApprovals (or XDG-ish on mac/linux for dev).</summary>
public static class ConfigDir
{
    public static string Path =>
        Environment.GetEnvironmentVariable("CLAUDE_APPROVALS_CONFIG")
        ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeApprovals");
}
