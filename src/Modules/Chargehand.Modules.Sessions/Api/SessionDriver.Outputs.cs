using System.Text.Json;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Sessions.Api;

public static partial class SessionDriver
{
    /// <summary>The <c>session-outcome.json</c> a session left in <paramref name="directory"/>; null when there is none or it is not an outcome.</summary>
    public static SessionOutcome? ReadOutcome(string directory)
    {
        var file = Path.Combine(directory, "session-outcome.json");
        if (!File.Exists(file))
            return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), LineJson.SessionOutcome);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every decision of the session's <c>governor.jsonl</c> in <paramref name="directory"/>, a line that cannot be read skipped; null when there is no log or it cannot be read.</summary>
    public static List<GovernorDecision>? ReadGovernorLog(string directory)
    {
        var file = Path.Combine(directory, GovernorFile);
        if (!File.Exists(file))
            return null;
        List<GovernorDecision> decisions = [];
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    if (JsonSerializer.Deserialize(line, LineJson.GovernorDecision) is { } decision)
                        decisions.Add(decision);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        return decisions;
    }
}
