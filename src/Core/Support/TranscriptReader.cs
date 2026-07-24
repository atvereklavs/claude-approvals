using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeApprovals.Core.Models;

/// <summary>
/// Best-effort "why Claude wants this": the most recent assistant text block in
/// the session transcript. Fail-silent: any error → null.
/// </summary>
public static class TranscriptReader
{
    public static string? LastAssistantText(string? path, int maxBytes = 512 * 1024)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();

            var lines = text.Split('\n');
            var searchable = start > 0 ? lines.Skip(1) : lines;

            foreach (var line in searchable.Reverse())
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || !trimmed.StartsWith('{')) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(trimmed); } catch { continue; }
                if (node is not JsonObject obj) continue;
                if (obj["type"]?.GetValue<string>() != "assistant") continue;
                if (obj["message"]?["content"] is not JsonArray content) continue;
                foreach (var block in content.Reverse())
                {
                    if (block?["type"]?.GetValue<string>() != "text") continue;
                    var t = block["text"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrEmpty(t))
                        return t.Length > 400 ? t[..400] : t;
                }
            }
        }
        catch { /* fail-silent */ }
        return null;
    }
}
