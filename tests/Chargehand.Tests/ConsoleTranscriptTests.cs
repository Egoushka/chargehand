using System.Net;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Server;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary><c>GET /v1/batches/{id}/tasks/{task}/sessions/{session}/transcript</c> (ADR 0059, CHARGEHAND-214): the end of one session's transcript, for the owner and the batch's account, redacted, never stored.</summary>
public class ConsoleTranscriptTests
{
    private const string Batch = "run-b1";
    private const string Marker = "UNIQUE-TRANSCRIPT-MARKER";
    private static readonly DateTimeOffset At = ConsoleRig.Now.AddHours(-2);

    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static AccountSettings Bob() => new("bob", Driven: true, AllowServerCredential: true);

    private static Profile WithCredential(string root) => Runs.Profile(root) with
    {
        ClaudeCode = new ClaudeCodeSettings("2.1.283", OauthTokenSecret: "claude-code-oauth-token"),
    };

    private static string Line(string text) => JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] { new { type = "text", text } } } });

    private sealed record Setup(ConsoleRig Rig, DrivenRunTests.FakeVolumes Volumes) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Rig.DisposeAsync();
    }

    private static async Task<Setup> StartAsync(string? actor = null, bool seed = true)
    {
        var volumes = new DrivenRunTests.FakeVolumes(_ => SessionStatus.Completed);
        var engine = new DrivenRunTests.FakeEngine();
        var rig = await ConsoleRig.StartAsync(accounts: [Alice(), Bob()], profile: WithCredential, services: _ => new DrivenServices(engine, engine, volumes, _ => throw new NotSupportedException(), null,
            (_, _, _) => Task.FromResult(new DrivenCheckout("/srv/checkouts/repo", ConsoleRig.Commit, "https://github.com/o/r.git", "main")), new DrivenRunTests.NoEvidenceProblems(), name => $"value-of-{name}"));
        if (seed)
        {
            await ConsoleSeed.BatchAsync(rig, Batch, actor, new SeedTask("t1", Goal: "Add a retry"), new SeedTask("t2", Goal: "Add a backoff"));
            rig.Records.Append(Batch, new SessionRecord("t1", "run-b1-s1", "first", null, At, At.AddMinutes(9), "completed", null, 20, 1, 1, 1, 1, null, null, 0, 2));
            rig.Records.Append(Batch, new SessionRecord("t1", "run-b1-fix1", "ci_fix", "vps", At.AddMinutes(20), At.AddMinutes(22), "completed", null, 5, 1, 1, 1, 1, null, null, 0, 3));
            rig.Records.Append(Batch, new SessionRecord("t2", "run-b1-s2", "first", null, At, At.AddMinutes(8), "completed", null, 20, 1, 1, 1, 1, null, null, 0, 2));
            volumes.Transcripts["run-b1-s1"] = Line($"first session {Marker} with value-of-claude-code-oauth-token") + "\n" + Line("then it printed AKIA" + new string('A', 16)) + "\n";
            volumes.Transcripts["run-b1-s2"] = Line("the other task") + "\n";
            volumes.Transcripts["run-b1-fix1"] = Line("the fix") + "\n";
        }
        return new Setup(rig, volumes);
    }

    private static string Route(string session = "run-b1-s1", string task = "t1", string batch = Batch, string query = "") => $"/v1/batches/{batch}/tasks/{task}/sessions/{session}/transcript{query}";

    [Fact]
    public async Task The_owner_reads_the_end_of_a_session_redacted_with_the_kinds_replaced_and_no_cache()
    {
        await using var s = await StartAsync();

        var response = await s.Rig.Owner.GetAsync(Route());
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("transcript/v1", body.GetProperty("contract_version").GetString());
        Assert.Equal((Batch, "t1"), (body.GetProperty("batch_run_id").GetString(), body.GetProperty("task_id").GetString()));
        Assert.Equal(("run-b1-s1", "first"), (body.GetProperty("session").GetProperty("run_id").GetString(), body.GetProperty("session").GetProperty("role").GetString()));
        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(65536, body.GetProperty("max_bytes").GetInt32());
        var lines = body.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains(Marker, lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("value-of-claude-code-oauth-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAAAAA", text, StringComparison.Ordinal);
        Assert.Equal(["aws", "credential-literal"], body.GetProperty("redacted").EnumerateArray().Select(k => k.GetString()));
        Assert.NotEmpty(body.GetProperty("not_recorded").EnumerateArray());
        Assert.Equal([("run-b1-s1", 65536)], s.Volumes.TranscriptReads);
    }

    [Fact]
    public async Task The_bytes_query_sets_the_window_and_a_value_outside_the_range_is_a_bad_request()
    {
        await using var s = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(s.Rig.Owner, Route(query: "?bytes=2048")));
        Assert.Equal(2048, s.Volumes.TranscriptReads.Single().MaxBytes);
        foreach (var bad in new[] { "100", "262145", "abc", "-1", "1e5" })
            Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(s.Rig.Owner, Route(query: $"?bytes={bad}")));
        Assert.Single(s.Volumes.TranscriptReads);
    }

    [Fact]
    public async Task An_account_reads_the_transcript_of_its_own_batch_and_another_account_gets_a_not_found_without_a_volume_read()
    {
        await using var s = await StartAsync("alice");
        using var alice = s.Rig.As("alice");
        using var bob = s.Rig.As("bob");

        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(bob, Route()));
        Assert.Empty(s.Volumes.TranscriptReads);
        Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(alice, Route()));
        Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(s.Rig.Owner, Route()));
        Assert.Equal(2, s.Volumes.TranscriptReads.Count);
    }

    [Fact]
    public async Task A_run_token_and_no_key_reach_nothing_and_no_volume_is_read()
    {
        await using var s = await StartAsync();
        var repo = Runs.GitRepo(s.Rig.Server.WorkerRoot);
        var token = s.Rig.Server.Tokens.Issue(new RunTokenClaims("run-task", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1), null), DateTimeOffset.UtcNow);
        using var session = s.Rig.Server.As(token);
        using var anonymous = new HttpClient { BaseAddress = s.Rig.Server.BaseAddress };

        Assert.Equal(HttpStatusCode.Forbidden, await ConsoleRig.Status(session, Route()));
        Assert.Equal(HttpStatusCode.Unauthorized, await ConsoleRig.Status(anonymous, Route()));
        Assert.Empty(s.Volumes.TranscriptReads);
    }

    [Fact]
    public async Task A_session_that_the_task_does_not_list_is_not_found_and_reaches_no_volume()
    {
        await using var s = await StartAsync();

        // Another task's session, an invented run id, a path-like one, an unknown task and an unknown batch.
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(s.Rig.Owner, Route("run-b1-s2")));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(s.Rig.Owner, Route("run-guessed")));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(s.Rig.Owner, Route("..%2F..%2Fetc")));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(s.Rig.Owner, Route(task: "t9")));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(s.Rig.Owner, Route(batch: "run-none")));
        Assert.Empty(s.Volumes.TranscriptReads);
    }

    [Fact]
    public async Task A_session_with_no_transcript_is_available_false_with_the_reason_and_not_an_error()
    {
        await using var s = await StartAsync();
        s.Volumes.Transcripts.Clear();

        var body = await ConsoleRig.Get(s.Rig.Owner, Route());

        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.Equal("session_gone", body.GetProperty("reason").GetString());
        Assert.Equal(0, body.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task A_session_recorded_on_a_runner_this_server_no_longer_has_says_so()
    {
        await using var s = await StartAsync();

        var body = await ConsoleRig.Get(s.Rig.Owner, Route("run-b1-fix1"));

        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.Equal("runner_unknown", body.GetProperty("reason").GetString());
        Assert.Equal("vps", body.GetProperty("session").GetProperty("runner").GetString());
        Assert.Empty(s.Volumes.TranscriptReads);
    }

    [Fact]
    public async Task A_runner_that_refuses_the_read_is_reported_as_too_old_without_its_message()
    {
        await using var s = await StartAsync();
        s.Volumes.TranscriptFails = new ChargehandException(ErrorCode.ContainerUnavailable, "the runner refused GET /out/run-b1-s1/stream.jsonl?image=registry.example/x&tail=65536: 403 the runner reads only chargehand.bundle");

        var text = await ConsoleRig.Text(s.Rig.Owner, Route());

        Assert.Contains("\"reason\":\"runner_too_old\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("registry.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tail=65536", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transcript_is_in_no_other_body_and_is_kept_nowhere_on_disk()
    {
        await using var s = await StartAsync();
        await ConsoleRig.Text(s.Rig.Owner, Route());

        foreach (var route in new[] { $"/v1/batches/{Batch}", $"/v1/batches/{Batch}/tasks/t1", "/v1/overview", "/v1/inbox", "/v1/pulls", "/v1/analytics", "/v1/system" })
            Assert.DoesNotContain(Marker, await ConsoleRig.Text(s.Rig.Owner, route), StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(s.Rig.Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Marker, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_that_is_still_running_is_listed_by_the_saved_state_and_its_transcript_is_read()
    {
        await using var s = await StartAsync(seed: false);
        var request = ConsoleSeed.BatchRequest(new DrivenTask("t1", Goal: "one"));
        await s.Rig.Log.AppendAsync(ConsoleSeed.Start("run-live", request, "alice", ConsoleRig.Now.AddMinutes(-20)), default);
        var started = ConsoleRig.Now.AddMinutes(-19);
        new BatchStateStore(s.Rig.BatchDirectory).Save(new BatchState("run-live", started, new string('0', 32), request, "TOKEN-KEY",
            [new TaskEntry("t1", null, "one", TaskPhase.Started, "run-s1", "chargehand/run-s1", "chargehand-run-s1", started, Runs: ["run-s1"])], [], Actor: "alice"));
        s.Volumes.Transcripts["run-s1"] = Line("working on it") + "\n";
        using var alice = s.Rig.As("alice");

        var text = await ConsoleRig.Text(alice, Route("run-s1", batch: "run-live"));

        Assert.Contains("working on it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN-KEY", text, StringComparison.Ordinal);
    }
}
