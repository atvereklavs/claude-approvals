namespace ClaudeApprovals.Core.Models;

public enum DiffKind { Context, Added, Removed }

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>
/// Lightweight line diff: trims the common prefix/suffix and shows the changed
/// middle as removed-then-added with context. Port of the Mac TextDiff.
/// </summary>
public static class TextDiff
{
    public static IReadOnlyList<DiffLine> Lines(string oldText, string newText, int context = 2)
    {
        var o = oldText.Split('\n');
        var n = newText.Split('\n');

        var start = 0;
        while (start < o.Length && start < n.Length && o[start] == n[start]) start++;
        var oEnd = o.Length;
        var nEnd = n.Length;
        while (oEnd > start && nEnd > start && o[oEnd - 1] == n[nEnd - 1]) { oEnd--; nEnd--; }

        var result = new List<DiffLine>();
        for (var i = Math.Max(0, start - context); i < start; i++)
            result.Add(new DiffLine(DiffKind.Context, o[i]));
        for (var i = start; i < oEnd; i++)
            result.Add(new DiffLine(DiffKind.Removed, o[i]));
        for (var i = start; i < nEnd; i++)
            result.Add(new DiffLine(DiffKind.Added, n[i]));
        for (var i = oEnd; i < Math.Min(o.Length, oEnd + context); i++)
            result.Add(new DiffLine(DiffKind.Context, o[i]));
        return result;
    }

    /// <summary>Every line added (Write tool: no prior state).</summary>
    public static IReadOnlyList<DiffLine> AllAdded(string text) =>
        text.Split('\n').Select(l => new DiffLine(DiffKind.Added, l)).ToList();

    /// <summary>Compact "+N −M" stat for headers.</summary>
    public static string Stat(IReadOnlyList<DiffLine> lines)
    {
        var added = lines.Count(l => l.Kind == DiffKind.Added);
        var removed = lines.Count(l => l.Kind == DiffKind.Removed);
        return $"+{added} −{removed}";
    }
}
