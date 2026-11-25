using System.Globalization;
using System.Text;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>Where a free model's data may go: <see cref="Private"/> models keep nothing and do not train (any repository may use them), <see cref="Public"/> ones may train (public repositories only).</summary>
public enum FreeTier { Private, Public }

/// <summary>What a free model is good for. The six classes of the maintainer's list (spec 2026-11-25).</summary>
public enum FreeTask { Ask, Review, Edit, Plan, Mechanical, Critic }

/// <summary>A grade for one class. <c>no</c> and an ungraded class never route, so only the two that do are here.</summary>
public enum FreeGrade { Good = 0, Ok = 1 }

/// <summary>One entry of the list: <paramref name="Id"/> is <c>provider/model</c> as the OpenCode server names it, split at the first slash only.</summary>
public sealed record FreeModel(string Id, FreeTier Tier, IReadOnlyDictionary<FreeTask, FreeGrade> Grades)
{
    public string Provider => Id[..Id.IndexOf('/', StringComparison.Ordinal)];
}

/// <summary>The maintainer's free-model list, read from a mounted TOML file (spec 2026-11-25, D1). The reader takes the subset the file uses (comments, tables, arrays of tables,
/// strings, integers, inline tables of strings) and nothing else: any line it does not understand fails the whole file, and a failed file offers no model.</summary>
public sealed class FreeModelCatalog
{
    public static readonly FreeModelCatalog Empty = new([]);

    private FreeModelCatalog(IReadOnlyList<FreeModel> models) => Models = models;

    /// <summary>In the file's order, which is the fallback order.</summary>
    public IReadOnlyList<FreeModel> Models { get; }

    /// <summary>The catalog of <paramref name="path"/>, or <see cref="Empty"/> with the reason when the file is missing, unreadable or malformed.</summary>
    public static (FreeModelCatalog Catalog, string? Problem) Load(string path)
    {
        try
        {
            return (Parse(File.ReadAllText(path)), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return (Empty, $"the free-model list is not usable: {e.Message}");
        }
    }

    public static FreeModelCatalog Parse(string toml)
    {
        var models = new List<Dictionary<string, object>>();
        Dictionary<string, object>? current = null;
        var lineNo = 0;
        foreach (var raw in toml.Split('\n'))
        {
            lineNo++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (line != "[[model]]")
                    throw new FormatException($"line {lineNo}: unknown array of tables {line}");
                current = [];
                models.Add(current);
            }
            else if (line.StartsWith('['))
            {
                current = null;
            }
            else
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                    throw new FormatException($"line {lineNo}: expected key = value");
                var key = line[..eq].Trim();
                var value = ParseValue(line[(eq + 1)..].Trim(), lineNo);
                // A repeated key is an error in TOML: the last one winning could turn a public model private.
                if (current is not null && !current.TryAdd(key, value))
                    throw new FormatException($"line {lineNo}: {key} is set twice");
            }
        }
        return new FreeModelCatalog([.. models.Select((m, i) => ToModel(m, i + 1))]);
    }

    private static FreeModel ToModel(Dictionary<string, object> m, int n)
    {
        var id = m.GetValueOrDefault("id") as string;
        if (string.IsNullOrEmpty(id) || id.IndexOf('/', StringComparison.Ordinal) < 1)
            throw new FormatException($"model {n}: id must be provider/model");
        var tier = m.GetValueOrDefault("tier") switch
        {
            "private" => FreeTier.Private,
            "public" => FreeTier.Public,
            var other => throw new FormatException($"{id}: unknown tier '{other}'"),
        };
        var grades = new Dictionary<FreeTask, FreeGrade>();
        if (m.GetValueOrDefault("tasks") is Dictionary<string, string> tasks)
        {
            foreach (var (task, grade) in tasks)
            {
                SetGrade(grades, id, TaskOf(task) ?? throw new FormatException($"{id}: unknown task class '{task}'"), grade);
            }
        }
        return new FreeModel(id, tier, grades);
    }

    private static FreeTask? TaskOf(string name) => name switch
    {
        "ask" => FreeTask.Ask,
        "review" => FreeTask.Review,
        "edit" => FreeTask.Edit,
        "plan" => FreeTask.Plan,
        "mechanical" => FreeTask.Mechanical,
        "critic" => FreeTask.Critic,
        _ => null,
    };

    private static void SetGrade(Dictionary<FreeTask, FreeGrade> grades, string id, FreeTask task, string grade)
    {
        switch (grade)
        {
            case "good": grades[task] = FreeGrade.Good; break;
            case "ok": grades[task] = FreeGrade.Ok; break;
            case "no": break;
            default: throw new FormatException($"{id}: unknown grade '{grade}' for {task}");
        }
    }

    private static object ParseValue(string text, int lineNo)
    {
        if (text.StartsWith('"'))
        {
            var s = ParseString(text, lineNo, out var end);
            return end == text.Length ? s : throw new FormatException($"line {lineNo}: text after a string");
        }
        if (text.StartsWith('{') && text.EndsWith('}'))
            return ParseInline(text[1..^1], lineNo);
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"line {lineNo}: unsupported value {text}");
    }

    private static Dictionary<string, string> ParseInline(string body, int lineNo)
    {
        var result = new Dictionary<string, string>();
        var rest = body.Trim();
        while (rest.Length > 0)
        {
            var eq = rest.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                throw new FormatException($"line {lineNo}: bad inline table");
            var key = rest[..eq].Trim();
            var value = rest[(eq + 1)..].TrimStart();
            if (!result.TryAdd(key, ParseString(value, lineNo, out var end)))
                throw new FormatException($"line {lineNo}: {key} is set twice");
            rest = value[end..].TrimStart();
            if (rest.StartsWith(','))
            {
                rest = rest[1..].TrimStart();
                if (rest.Length == 0)
                    throw new FormatException($"line {lineNo}: trailing comma in an inline table");
            }
            else if (rest.Length > 0)
            {
                throw new FormatException($"line {lineNo}: bad inline table");
            }
        }
        return result;
    }

    /// <summary>A basic string at the start of <paramref name="text"/>; <paramref name="end"/> is the index after its closing quote. Only <c>\"</c> and <c>\\</c> are escapes.</summary>
    private static string ParseString(string text, int lineNo, out int end)
    {
        if (!text.StartsWith('"'))
            throw new FormatException($"line {lineNo}: expected a string");
        var sb = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                end = i + 1;
                return sb.ToString();
            }
            if (c == '\\')
            {
                if (i + 1 >= text.Length || text[i + 1] is not ('"' or '\\'))
                    throw new FormatException($"line {lineNo}: unsupported escape");
                c = text[++i];
            }
            sb.Append(c);
        }
        throw new FormatException($"line {lineNo}: unterminated string");
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && inString)
                i++;
            else if (line[i] == '"')
                inString = !inString;
            else if (line[i] == '#' && !inString)
                return line[..i];
        }
        return line;
    }
}
