using System.Globalization;

namespace Chargehand.Automation;

/// <summary>One <c>[kind.NAME]</c> of the workspace <c>work-kinds.toml</c>: who runs it, with which model class, gates and budget, and whether it may start at night.</summary>
public sealed record WorkKind(string Name, string Runner, string ModelClass, IReadOnlyList<string> Gates, decimal BudgetUsd, int MaxMinutes, bool Night);

/// <summary>Reads the fields the board producer needs from <c>work-kinds.toml</c>. Not a general TOML reader: one <c>key = value</c> per line, strings, numbers, booleans and string arrays.</summary>
public static class WorkKinds
{
    public static IReadOnlyDictionary<string, WorkKind> Parse(string toml)
    {
        var kinds = new Dictionary<string, WorkKind>(StringComparer.Ordinal);
        string? name = null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[')
            {
                Flush();
                name = line.StartsWith("[kind.", StringComparison.Ordinal) && line.EndsWith(']') ? line[6..^1] : null;
                continue;
            }
            if (name is not null && line.IndexOf('=') is > 0 and var eq)
                fields[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        Flush();
        return kinds;

        void Flush()
        {
            if (name is not null)
                kinds[name] = new WorkKind(name, Str("runner"), Str("model_class"), List("gates"), decimal.TryParse(Raw("budget_usd"), NumberStyles.Number, CultureInfo.InvariantCulture, out var b) ? b : 0m,
                    int.TryParse(Raw("max_minutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0, Raw("night") == "true");
            fields.Clear();
        }

        string Raw(string key) => fields.TryGetValue(key, out var v) ? v : "";
        string Str(string key) => Raw(key).Trim('"');
        List<string> List(string key) => Raw(key).Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Trim('"')).ToList();
    }
}
