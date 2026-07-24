using System.Text.Json.Nodes;

namespace ClaudeApprovals.Core.Models;

/// <summary>Parsed AskUserQuestion input: real questions + options, not raw JSON.</summary>
public sealed class AskContent
{
    public sealed record Option(string Label, string Description);

    public sealed record Question(string Text, string Header, bool MultiSelect, IReadOnlyList<Option> Options);

    public IReadOnlyList<Question> Questions { get; }

    private AskContent(IReadOnlyList<Question> questions) => Questions = questions;

    public Question? First => Questions.Count > 0 ? Questions[0] : null;

    public static AskContent? Parse(JsonNode? input)
    {
        if (input is not JsonObject obj || obj["questions"] is not JsonArray qs || qs.Count == 0)
            return null;

        var questions = new List<Question>();
        foreach (var qNode in qs)
        {
            if (qNode is not JsonObject q) continue;
            var options = new List<Option>();
            if (q["options"] is JsonArray opts)
            {
                foreach (var oNode in opts)
                {
                    if (oNode is not JsonObject o) continue;
                    options.Add(new Option(GetStr(o, "label"), GetStr(o, "description")));
                }
            }
            var multi = q["multiSelect"] is JsonValue mv && mv.TryGetValue<bool>(out var b) && b;
            questions.Add(new Question(GetStr(q, "question"), GetStr(q, "header"), multi, options));
        }
        return new AskContent(questions);
    }

    private static string GetStr(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
