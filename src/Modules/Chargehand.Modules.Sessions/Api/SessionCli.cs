using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Sessions.Api;

/// <summary><c>chargehand session</c>: the command a session container runs (ADR 0039). It reads <c>/out/task.json</c> (written by the runner's
/// side before the container starts), runs <see cref="SessionDriver"/> in <c>/work</c> and leaves its files in <c>/out</c>. The outcome file, not the
/// exit code, is the record: the exit code is 0 when the session completed or needs input, 1 otherwise. <c>--probe &lt;file&gt;</c> instead measures the image's tools and writes the manifest the
/// environment note reads (ADR 0048); the image build runs it.</summary>
public static class SessionCli
{
    internal static readonly SessionsJsonContext Context = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // The server and the session image deploy separately: a field one side learned first must not stop the other (task.json, session-usage.json).
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    });

    /// <summary>The options of <c>task.json</c> and <c>session-usage.json</c>, bound to the session's source-generated types.</summary>
    public static JsonSerializerOptions Json => Context.Options;

    /// <summary>Appended to Claude Code's system prompt. It lives in code, in the trusted build, so a pull request cannot change how the
    /// session that reviews it is told to behave (the same reason as the support judge's prompt).</summary>
    public const string DefaultPrompt = """
        You are running headless inside a container, on a task chargehand started. There is no person to answer questions.
        Follow the /chargehand:change steps as written, with these changes:
        - Where a step would ask the user something, stop working and print a line `NEEDS_INPUT:` followed by the questions, one per line. Then finish.
        - The branch named in the environment variable CHARGEHAND_BRANCH already exists and is checked out: use it instead of choosing a `change/<slug>` name.
        - Research and review call `orchestrate` with contract_version, text and context as top-level arguments (no `request` wrapper), and context.repository = {path: $CHARGEHAND_REPOSITORY_PATH, commit: $CHARGEHAND_BASE_COMMIT} (never /work or HEAD; the review's
          diff is `git diff $CHARGEHAND_BASE_COMMIT..HEAD`). Never skip research or review: if a call is refused, fix it from the error; if chargehand stays unreachable, `NEEDS_INPUT:`.
        - Order: research, write, run the tests, then review; never review before the tests ran. If the goal cannot be met, still write your best attempt and run the tests, then
          finish saying why it cannot be done. Never stop after research alone and never edit a test to make it pass.
        - The working tree is already clean; do not stop for uncommitted changes.
        - Do not push, do not open a pull request, do not merge: chargehand does that after it checks your branch itself.
        - Run the tests in the foreground with a long enough timeout for them to finish, even a slow suite: never start them as a background task and never end your turn to wait for one,
          because ending your turn ends this session and the work in that background task is lost with no report. Print your report before you finish, not after.
        - Finish with one fenced ```json block: {"summary": string, "claims": [{"text": string, "evidence": [ "path:start-end" ]}], "tests": {"command": string, "exit_code": number}}.
          Each claim names the code lines it rests on. Say what you did not verify instead of guessing.
        """;

    private static readonly string[] SecretVariables = ["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "CHARGEHAND_RUN_TOKEN"];

    public static SessionTask ReadTask(string path)
    {
        try
        {
            var task = JsonSerializer.Deserialize(File.ReadAllText(path), Context.SessionTask) ?? throw new InvalidDataException("empty task file");
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
        var manifestPath = ToolchainManifest.DefaultPath;
        string? probe = null;
        for (var i = 0; i + 1 < args.Count; i += 2)
            switch (args[i])
            {
                case "--work": work = args[i + 1]; break;
                case "--out": output = args[i + 1]; break;
                case "--toolchains": manifestPath = args[i + 1]; break;
                case "--probe": probe = args[i + 1]; break;
                default: error.WriteLine("usage: chargehand session [--work <dir>] [--out <dir>] [--toolchains <file>] | --probe <file>"); return 2;
            }
        if (probe is not null)
        {
            await File.WriteAllTextAsync(probe, (await ToolchainManifest.ProbeAsync(ct)).ToJson(), ct);
            return 0;
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
        // The note is the manifest the image build wrote, else one probe by the driver: the model never has to look. It sits after the static text, which stays as Prompt CI saw it.
        var toolchains = ToolchainManifest.Load(manifestPath) ?? await ToolchainManifest.ProbeAsync(ct);
        var options = new SessionOptions(work, output, task, SessionCommand.Claude, secrets, $"{DefaultPrompt}\n\n{EnvironmentNote.Render(toolchains, task.Environment)}",
            Environment.GetEnvironmentVariable("CHARGEHAND_MCP_URL"), Environment.GetEnvironmentVariable("CHARGEHAND_RUN_TOKEN"));
        var outcome = await SessionDriver.RunAsync(options, ct);
        error.WriteLine($"session {outcome.Status} {outcome.Reason}");
        return outcome.Status is SessionStatus.Completed or SessionStatus.NeedsInput ? 0 : 1;
    }
}
