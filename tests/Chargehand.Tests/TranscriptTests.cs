using System.Text;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Runner;

namespace Chargehand.Tests;

/// <summary>ADR 0059: the end of a session's transcript, made safe to show: whole lines, long ones cut, secrets redacted by pattern and by the credentials the server holds.</summary>
public class TranscriptTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Event(string text) => JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] { new { type = "text", text } } } });

    // The shapes of the secrets the scanner knows, built here so that no test file holds a credential-looking literal on one line.
    private static string Aws => "AKIA" + new string('A', 16);
    private static string Github => "ghp_" + new string('b', 36);
    private static string Anthropic => "sk-ant-" + new string('c', 30);
    private static string Begin => "-----" + "BEGIN";
    private static string End => "-----" + "END";
    private const string Key = "PRIVATE KEY";
    private static string Slack => "xoxb-" + new string('1', 12);

    [Fact]
    public void A_file_smaller_than_the_window_is_whole_and_not_truncated()
    {
        var text = Event("one") + "\n" + Event("two") + "\n";

        var view = Transcript.Prepare(Bytes(text), 65536, []);

        Assert.Equal([Event("one"), Event("two")], view.Lines);
        Assert.False(view.Truncated);
        Assert.Equal(Bytes(text).Length, view.Bytes);
    }

    [Fact]
    public void A_window_that_began_inside_a_line_drops_that_partial_line_and_says_earlier_lines_exist()
    {
        var whole = Event("first line of the session") + "\n" + Event("second") + "\n" + Event("third") + "\n";
        var window = Bytes(whole)[^(Bytes(Event("second")).Length + Bytes(Event("third")).Length + 4)..];

        var view = Transcript.Prepare(window, window.Length, []);

        Assert.True(view.Truncated);
        Assert.Equal([Event("second"), Event("third")], view.Lines);
    }

    [Fact]
    public void A_window_that_began_inside_a_multi_byte_character_loses_only_its_first_line()
    {
        var whole = "éé the rest of a long first line\n" + Event("kept") + "\n";
        var window = Bytes(whole)[1..];             // inside the first character of the first line: a lone continuation byte

        var view = Transcript.Prepare(window, window.Length, []);

        Assert.Equal([Event("kept")], view.Lines);
    }

    [Fact]
    public void A_window_with_no_complete_line_is_empty_and_truncated()
    {
        var view = Transcript.Prepare(Bytes("middle of one very long line"), 28, []);

        Assert.Empty(view.Lines);
        Assert.True(view.Truncated);
    }

    [Fact]
    public void An_empty_file_and_blank_lines_give_no_lines_and_crlf_is_trimmed()
    {
        Assert.Empty(Transcript.Prepare([], 4096, []).Lines);
        Assert.Equal(["a", "b"], Transcript.Prepare(Bytes("a\r\n\n\r\nb\r\n"), 4096, []).Lines);
    }

    [Fact]
    public void A_line_over_the_limit_is_replaced_by_a_stub_that_gives_its_size()
    {
        var huge = Event(new string('x', Transcript.MaxLine));
        var view = Transcript.Prepare(Bytes(Event("small") + "\n" + huge + "\n" + Event("after") + "\n"), 1_000_000, []);

        Assert.Equal(3, view.Lines.Count);
        Assert.Equal(1, view.Cut);
        using var stub = JsonDocument.Parse(view.Lines[1]);
        Assert.Equal("chargehand_cut", stub.RootElement.GetProperty("type").GetString());
        Assert.Equal(Bytes(huge).Length, stub.RootElement.GetProperty("bytes").GetInt32());
        Assert.DoesNotContain("xxxxxxxxxx", view.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Every_secret_shape_the_push_scanner_knows_is_replaced_and_the_line_stays_valid_json()
    {
        var line = Event($"keys: {Aws} {Github} {Anthropic} {Slack} done");

        var view = Transcript.Prepare(Bytes(line + "\n"), 65536, []);

        var shown = Assert.Single(view.Lines);
        foreach (var secret in new[] { Aws, Github, Anthropic, Slack })
            Assert.DoesNotContain(secret, shown, StringComparison.Ordinal);
        Assert.Contains("[redacted:aws]", shown, StringComparison.Ordinal);
        Assert.Contains("[redacted:github-token]", shown, StringComparison.Ordinal);
        Assert.Contains("[redacted:anthropic-key]", shown, StringComparison.Ordinal);
        Assert.Contains("[redacted:slack-token]", shown, StringComparison.Ordinal);
        Assert.Equal(["anthropic-key", "aws", "github-token", "slack-token"], view.Redacted);
        using var parsed = JsonDocument.Parse(shown);
        Assert.StartsWith("keys: [redacted:aws]", parsed.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_key_is_taken_from_its_header_to_its_footer_and_an_unterminated_one_to_the_end_of_its_string()
    {
        var body = string.Concat("MIIE", new string('Q', 80));
        var complete = Event($"{Begin} RSA {Key}-----\n{body}\n{End} RSA {Key}-----\nand then more");
        var unterminated = Event($"{Begin} {Key}-----\n{body}");

        var view = Transcript.Prepare(Bytes(complete + "\n" + unterminated + "\n"), 65536, []);

        Assert.All(view.Lines, l =>
        {
            Assert.DoesNotContain(body, l, StringComparison.Ordinal);
            JsonDocument.Parse(l).Dispose();
        });
        Assert.Contains("and then more", view.Lines[0], StringComparison.Ordinal);
        Assert.Equal(["private-key"], view.Redacted);
    }

    [Fact]
    public void A_held_credential_is_replaced_wherever_it_appears_also_when_json_escaped_and_a_short_value_is_ignored()
    {
        const string key = "sk-live/abc\"def-12345678";
        var line = Event($"the key is {key} and again {key}; short: abc12");

        var view = Transcript.Prepare(Bytes(line + "\n"), 65536, [key, "abc12"]);

        var shown = Assert.Single(view.Lines);
        Assert.DoesNotContain("sk-live", shown, StringComparison.Ordinal);
        Assert.Equal(2, shown.Split("[redacted]").Length - 1);
        Assert.Contains("short: abc12", shown, StringComparison.Ordinal);
        Assert.Equal(["credential-literal"], view.Redacted);
        JsonDocument.Parse(shown).Dispose();
    }

    [Fact]
    public void A_line_with_nothing_to_hide_is_returned_as_it_was_and_nothing_is_listed_as_redacted()
    {
        var view = Transcript.Prepare(Bytes(Event("dotnet test: 3 passed") + "\n"), 65536, ["a-held-credential-value"]);

        Assert.Equal([Event("dotnet test: 3 passed")], view.Lines);
        Assert.Empty(view.Redacted);
        Assert.Equal(0, view.Cut);
    }

    // DrivenRun.ReadTranscriptAsync

    [Fact]
    public async Task The_run_reads_a_sessions_transcript_and_redacts_the_credentials_it_holds_for_the_batch()
    {
        using var rig = new DrivenRunTests.Rig();
        rig.Volumes.Transcripts["run-s1"] = Event("token value-of-claude-code-oauth-token, push value-of-secret-push, ok") + "\n";

        var read = await rig.Run.ReadTranscriptAsync("run-s1", null, 65536, null, default);

        Assert.Null(read.Unavailable);
        var line = Assert.Single(read.View!.Lines);
        Assert.DoesNotContain("value-of-", line, StringComparison.Ordinal);
        Assert.Contains("ok", line, StringComparison.Ordinal);
        Assert.Equal(["credential-literal"], read.View.Redacted);
    }

    [Fact]
    public async Task An_accounts_own_model_key_is_redacted_for_its_batch_and_only_that_accounts()
    {
        AccountSettings alice = new("alice", Driven: true, Model: new AccountModel(ApiKeySecret: "alice-key-item"));
        using var rig = new DrivenRunTests.Rig(accounts: [alice]);
        rig.Volumes.Transcripts["run-s1"] = Event("it printed value-of-alice-key-item") + "\n";

        var ofAlice = await rig.Run.ReadTranscriptAsync("run-s1", null, 65536, "alice", default);
        var ofOwner = await rig.Run.ReadTranscriptAsync("run-s1", null, 65536, null, default);

        Assert.DoesNotContain("value-of-alice-key-item", ofAlice.View!.Lines[0], StringComparison.Ordinal);
        Assert.Contains("value-of-alice-key-item", ofOwner.View!.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_window_is_kept_between_the_floor_and_the_ceiling()
    {
        using var rig = new DrivenRunTests.Rig();
        rig.Volumes.Transcripts["run-s1"] = Event("x") + "\n";

        await rig.Run.ReadTranscriptAsync("run-s1", null, 1, null, default);
        await rig.Run.ReadTranscriptAsync("run-s1", null, int.MaxValue, null, default);

        Assert.Equal([Transcript.MinBytes, Transcript.MaxBytes], rig.Volumes.TranscriptReads.Select(r => r.MaxBytes));
    }

    [Fact]
    public async Task A_session_with_no_transcript_a_runner_that_refuses_and_one_that_is_gone_each_say_why()
    {
        using var rig = new DrivenRunTests.Rig();

        Assert.Equal("session_gone", (await rig.Run.ReadTranscriptAsync("run-none", null, 65536, null, default)).Unavailable);
        rig.Volumes.TranscriptFails = new ChargehandException(ErrorCode.ContainerUnavailable, "the runner refused GET /out/run-s1/stream.jsonl?tail=1: 403 the runner reads only chargehand.bundle");
        Assert.Equal("runner_too_old", (await rig.Run.ReadTranscriptAsync("run-s1", null, 65536, null, default)).Unavailable);
        rig.Volumes.TranscriptFails = new RunnerUnreachableException("the runner could not be reached: no route");
        Assert.Equal("runner_unreachable", (await rig.Run.ReadTranscriptAsync("run-s1", null, 65536, null, default)).Unavailable);
        rig.Volumes.TranscriptFails = new ChargehandException(ErrorCode.ContainerUnavailable, "reading the end of stream.jsonl from the output volume failed");
        Assert.Equal("runner_error", (await rig.Run.ReadTranscriptAsync("run-s1", null, 65536, null, default)).Unavailable);
        Assert.Equal("runner_unknown", (await rig.Run.ReadTranscriptAsync("run-s1", "no-such-runner", 65536, null, default)).Unavailable);
        using var off = new DrivenRunTests.Rig(enabled: false);
        Assert.Equal("driven_off", (await off.Run.ReadTranscriptAsync("run-s1", null, 65536, null, default)).Unavailable);
    }

    [Fact]
    public async Task The_unavailable_reason_never_carries_the_runners_own_message()
    {
        using var rig = new DrivenRunTests.Rig();
        rig.Volumes.TranscriptFails = new ChargehandException(ErrorCode.ContainerUnavailable, "refused http://10.1.2.3:4310/out/run-s1/stream.jsonl?image=secret-image: 500 boom");

        var read = await rig.Run.ReadTranscriptAsync("run-s1", null, 65536, null, default);

        Assert.Null(read.View);
        Assert.Equal("runner_error", read.Unavailable);
    }
}
