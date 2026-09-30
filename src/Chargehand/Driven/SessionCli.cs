using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Driven;

/// <summary><c>chargehand session</c>: the command a session container runs (ADR 0039). It reads <c>/out/task.json</c> (written by the runner's
/// side before the container starts), runs <see cref="SessionDriver"/> in <c>/work</c> and leaves its files in <c>/out</c>. The outcome file, not the
/// exit code, is the record: the exit code is 0 when the session completed or needs input, 1 otherwise.</summary>
public static class SessionCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Appended to Claude Code's system prompt. It lives in code, in the trusted build, so a pull request cannot change how the
    /// session that reviews it is told to behave (the same reason as the support judge's prompt).</summary>
    public const string DefaultPrompt = """
        You are running headless inside a container, on a task chargehand started. There is no person to answer questions.
        Follow the /chargehand:change steps as written, with these changes:
        - Where a step would ask the user something, stop working and print a line `NEEDS_INPUT:` followed by the questions, one per line. Then finish.
        - Create and use the branch named in the environment variable CHARGEHAND_BRANCH instead of choosing a `change/<slug>` name.
        - The working tree is already clean; do not stop for uncommitted changes.
        - Do not push, do not open a pull request, do not merge: chargehand does that after it checks your branch itself.
        - Finish with one fenced ```json block: {"summary": string, "claims": [{"text": string, "evidence": [ "path:start-end" ]}], "tests": {"command": string, "exit_code": number}}.
          Each claim names the code lines it rests on. Say what you did not verify instead of guessing.
        """;

    private static readonly string[] SecretVariables = ["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "CHARGEHAND_RUN_TOKEN"];

    public static SessionTask ReadTask(string path)
    {
        try
        {
            var task = JsonSerializer.Deserialize<SessionTask>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty task file");
            if (string.IsNullOrWhiteSpace(task.Goal) || string.IsNullOrWhiteSpace(task.RunId))
                throw new InvalidDataException("the task needs a run id and a goal");
            if (!task.Branch.StartsWith("chargehand/", StringComparison.Ordinal))
                throw new InvalidDataException("the branch must be under chargehand/");
            if (task.MaxTurns < 1 || task.MaxMinutes < 1 || task.NoProgressSeconds < 1)
                throw new InvalidDataException("turn, minute and no-progress limits must be positive");
            return task;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter error, CancellationToken ct)
    {
        var work = "/work";
        var output = "/out";
        for (var i = 0; i + 1 < args.Count; i += 2)
            switch (args[i])
            {
                case "--work": work = args[i + 1]; break;
                case "--out": output = args[i + 1]; break;
                default: error.WriteLine("usage: chargehand session [--work <dir>] [--out <dir>]"); return 2;
            }
        SessionTask task;
        try
        {
            task = ReadTask(Path.Combine(output, "task.json"));
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            error.WriteLine(e.Message);
            return 2;
        }
        var secrets = SecretVariables
            .Select(Environment.GetEnvironmentVariable).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
        var options = new SessionOptions(work, output, task, SessionCommand.Claude, secrets, DefaultPrompt,
            Environment.GetEnvironmentVariable("CHARGEHAND_MCP_URL"), Environment.GetEnvironmentVariable("CHARGEHAND_RUN_TOKEN"));
        var outcome = await SessionDriver.RunAsync(options, ct);
        error.WriteLine($"session {outcome.Status} {outcome.Reason}");
        return outcome.Status is SessionStatus.Completed or SessionStatus.NeedsInput ? 0 : 1;
    }
}
