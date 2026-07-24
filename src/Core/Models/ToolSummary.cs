using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeApprovals.Core.Models;

/// <summary>
/// Decision-sufficient summary of a pending tool call: everything needed to
/// approve/deny without switching windows. Port of the Mac ToolSummary.
/// </summary>
public sealed class ToolSummary
{
    public required string Title { get; init; }
    public required string Icon { get; init; }          // symbolic name; App maps to a glyph
    public required string Detail { get; init; }
    public string? Cwd { get; init; }
    public string? Reason { get; init; }
    public string? FullText { get; init; }
    public IReadOnlyList<DiffLine>? Diff { get; init; }
    public AskContent? Ask { get; init; }
    public string? Plan { get; init; }

    private const int MaxDiffLines = 60;

    public static ToolSummary Make(HookPayload p)
    {
        var cwd = Abbreviate(p.Cwd);
        switch (p.ToolName)
        {
            case "Bash":
            {
                var cmd = p.InputString("command") ?? "";
                return new ToolSummary
                {
                    Title = "Run command", Icon = "terminal",
                    Detail = FirstLine(cmd), Cwd = cwd,
                    Reason = p.InputString("description"), FullText = cmd,
                };
            }
            case "Edit":
            {
                var path = p.InputString("file_path") ?? "";
                var diff = TextDiff.Lines(p.InputString("old_string") ?? "", p.InputString("new_string") ?? "");
                return new ToolSummary
                {
                    Title = $"Edit {Basename(path)}", Icon = "pencil",
                    Detail = path, Cwd = cwd,
                    Reason = TranscriptReader.LastAssistantText(p.TranscriptPath),
                    Diff = Cap(diff),
                };
            }
            case "MultiEdit":
            {
                var path = p.InputString("file_path") ?? "";
                var lines = new List<DiffLine>();
                var count = 0;
                if (p.ToolInput is JsonObject o && o["edits"] is JsonArray edits)
                {
                    foreach (var e in edits)
                    {
                        if (e is not JsonObject eo) continue;
                        if (count > 0) lines.Add(new DiffLine(DiffKind.Context, "…"));
                        lines.AddRange(TextDiff.Lines(Str(eo, "old_string"), Str(eo, "new_string")));
                        count++;
                    }
                }
                return new ToolSummary
                {
                    Title = $"Edit {Basename(path)} · {count} changes", Icon = "pencil",
                    Detail = path, Cwd = cwd,
                    Reason = TranscriptReader.LastAssistantText(p.TranscriptPath),
                    Diff = Cap(lines),
                };
            }
            case "Write":
            {
                var path = p.InputString("file_path") ?? "";
                return new ToolSummary
                {
                    Title = $"Write {Basename(path)}", Icon = "file-plus",
                    Detail = path, Cwd = cwd,
                    Reason = TranscriptReader.LastAssistantText(p.TranscriptPath),
                    Diff = Cap(TextDiff.AllAdded(p.InputString("content") ?? "")),
                };
            }
            case "Read":
            {
                var path = p.InputString("file_path") ?? "";
                return new ToolSummary
                {
                    Title = $"Read {Basename(path)}", Icon = "file",
                    Detail = path, Cwd = cwd, FullText = path,
                };
            }
            case "WebFetch":
            {
                var url = p.InputString("url") ?? "";
                return new ToolSummary
                {
                    Title = "Fetch URL", Icon = "globe", Detail = url, Cwd = cwd,
                    Reason = p.InputString("prompt"), FullText = url,
                };
            }
            case "WebSearch":
            {
                var q = p.InputString("query") ?? "";
                return new ToolSummary { Title = "Web search", Icon = "search", Detail = q, Cwd = cwd, FullText = q };
            }
            case "AskUserQuestion":
            {
                var ask = AskContent.Parse(p.ToolInput);
                var header = ask?.First?.Header;
                return new ToolSummary
                {
                    Title = string.IsNullOrEmpty(header) ? "Question" : header!,
                    Icon = "question", Detail = ask?.First?.Text ?? "", Cwd = cwd, Ask = ask,
                };
            }
            case "ExitPlanMode":
            {
                return new ToolSummary
                {
                    Title = "Implementation plan", Icon = "checklist",
                    Detail = "", Cwd = cwd, Plan = p.InputString("plan") ?? "",
                };
            }
            default:
            {
                var tool = p.ToolName ?? "Tool";
                if (tool.StartsWith("mcp__"))
                {
                    var label = string.Join(" · ", tool.Split('_', StringSplitOptions.RemoveEmptyEntries).Skip(1));
                    return new ToolSummary
                    {
                        Title = $"MCP: {label}", Icon = "puzzle",
                        Detail = CompactInput(p.ToolInput), Cwd = cwd, FullText = CompactInput(p.ToolInput),
                    };
                }
                return new ToolSummary
                {
                    Title = tool, Icon = "wrench",
                    Detail = CompactInput(p.ToolInput), Cwd = cwd, FullText = CompactInput(p.ToolInput),
                };
            }
        }
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static IReadOnlyList<DiffLine> Cap(IReadOnlyList<DiffLine> lines)
    {
        if (lines.Count <= MaxDiffLines) return lines;
        var outLines = lines.Take(MaxDiffLines).ToList();
        outLines.Add(new DiffLine(DiffKind.Context, $"… ({lines.Count - MaxDiffLines} more lines)"));
        return outLines;
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOf('\n');
        return idx < 0 ? s : s[..idx];
    }

    private static string Basename(string path)
    {
        var b = Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(b) ? path : b;
    }

    private static string? Abbreviate(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home) ? "~" + path[home.Length..] : path;
    }

    private static string CompactInput(JsonNode? input)
    {
        if (input is null) return "";
        var s = input.ToJsonString();
        return s.Length > 500 ? s[..500] : s;
    }
}
