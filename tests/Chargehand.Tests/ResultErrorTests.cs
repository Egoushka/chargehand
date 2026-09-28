using System.Net;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.OpenCode;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>ADR 0022: a failed result carries a code a client can branch on, whatever threw.</summary>
public class ResultErrorTests
{
    public static TheoryData<Exception, ErrorCode> Failures() => new()
    {
        { new ChargehandException(ErrorCode.RuntimeUnavailable, "the OpenCode server is not reachable", "Start it: scripts/opencode-serve.sh <binary> <config> 4096"), ErrorCode.RuntimeUnavailable },
        { new OpenCodeException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailableError", "ConnectionRefused"), ErrorCode.ProviderUnavailable },
        { new OpenCodeException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailableError", "Rate limit exceeded"), ErrorCode.RateLimited },
        { new InvalidOperationException("claude -p exited 1: API Error: 429 {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"}}"), ErrorCode.RateLimited },
        { new InvalidOperationException("something else"), ErrorCode.Internal },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_run_that_throws_fails_with_the_code_of_the_exception(Exception failure, ErrorCode code)
    {
        using var dir = new TempDir();
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        runtime.GenerateFailures.Enqueue(failure);

        var r = await Runs.Orchestrator(runtime, dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl"))).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(code, r.Error?.Code);
        Assert.Equal(failure.Message, r.Error!.Message);
        Assert.Equal(ChargehandException.Retryable(code), r.Error.Retryable);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, JsonSerializer.SerializeToElement(r, ContractJson.Options)));
    }

    [Fact]
    public async Task An_unreachable_runtime_says_how_to_start_it()
    {
        using var dir = new TempDir();
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        runtime.GenerateFailures.Enqueue(new ChargehandException(ErrorCode.RuntimeUnavailable, "down", "Start it: scripts/opencode-serve.sh <binary> <config> 4096"));

        var r = await Runs.Orchestrator(runtime, dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl"))).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        Assert.Equal(new ResultError(ErrorCode.RuntimeUnavailable, "down", true, "Start it: scripts/opencode-serve.sh <binary> <config> 4096"), r.Error);
    }

    [Fact]
    public async Task An_unknown_preset_is_an_invalid_request()
    {
        using var dir = new TempDir();
        var request = Runs.DraftRequest() with { Context = new RequestContext(false, "no-such-preset") };

        var r = await Runs.Orchestrator(new ScriptedRuntime(Runs.DraftReply), dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl"))).RunAsync(request, CancellationToken.None);

        Assert.Equal(ErrorCode.InvalidRequest, r.Error?.Code);
        Assert.False(r.Error!.Retryable);
    }

    [Fact]
    public async Task A_completed_run_carries_no_error()
    {
        using var dir = new TempDir();
        var r = await Runs.Orchestrator(new ScriptedRuntime(Runs.DraftReply), dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl"))).RunAsync(Runs.DraftRequest(), CancellationToken.None);
        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData("Budget has been exceeded! Key=team-key (sk-...abcd) Current cost: 5.01, Max budget: 5.0",
        "Budget has been exceeded! Key=[redacted] (sk-[redacted]) Current cost: [redacted], Max budget: [redacted]")]
    [InlineData("401 Authorization: Bearer abc.def-ghi rejected", "401 Authorization: Bearer [redacted] rejected")]
    [InlineData("{\"api_key\": \"sk-short\", \"error\": \"invalid\"}", "{\"api_key\": \"[redacted]\", \"error\": \"invalid\"}")]
    [InlineData("Received API Key = sk-short", "Received API Key = [redacted]")]
    [InlineData("ConnectionRefused", "ConnectionRefused")]
    public void Scrub_removes_keys_aliases_tokens_and_spend(string text, string expected) =>
        Assert.Equal(expected, ChargehandException.Scrub(text));

    [Theory]
    [InlineData("intake returned no valid task-spec/v1 after one retry: bad json")]
    [InlineData("cannot read /repo/disk-cache/risk-model.json")]
    [InlineData("The user: denied the request")]
    public void Scrub_leaves_ordinary_words_that_merely_contain_a_key_shape_alone(string text) =>
        Assert.Equal(text, ChargehandException.Scrub(text));

    [Theory]
    [InlineData("Received API Key = sk-abcd1234, Key Hash (Token) =7f3a9c2b1e8d4f6a",
        "Received API Key = [redacted], Key Hash (Token) =[redacted]")]
    [InlineData("TeamMember=platform-alice team_member=bob organization=acme-corp",
        "TeamMember=[redacted] team_member=[redacted] organization=[redacted]")]
    [InlineData("team_alias=finance user_id=42 user_api_key_alias=xyz789",
        "team_alias=[redacted] user_id=[redacted] user_api_key_alias=[redacted]")]
    [InlineData("{\"apiKey\": \"abc123secret\"}", "{\"apiKey\": \"[redacted]\"}")]
    [InlineData("token: Bearer abc.def", "token: Bearer [redacted]")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz rejected", "Authorization: Basic [redacted] rejected")]
    [InlineData("Current cost: 5e-05, Max budget: 5.0", "Current cost: [redacted], Max budget: [redacted]")]
    public void Scrub_catches_real_gateway_leak_shapes(string text, string expected) =>
        Assert.Equal(expected, ChargehandException.Scrub(text));

    [Fact]
    public void A_gateway_budget_refusal_keeps_its_reason_and_says_what_to_do()
    {
        var e = new OpenCodeException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailableError",
            "Budget has been exceeded! Key=team-key (sk-...abcd) Current cost: 5.01, Max budget: 5.0");

        Assert.Equal(ErrorCode.ProviderUnavailable, e.Code);
        Assert.Equal("OpenCode 503 ServiceUnavailableError: Budget has been exceeded! Key=[redacted] (sk-[redacted]) Current cost: [redacted], Max budget: [redacted]", e.Message);
        Assert.Equal(OpenCodeException.BudgetAction, e.Action);
    }

    [Fact]
    public async Task A_run_that_fails_at_intake_keeps_keys_out_of_the_result_and_the_run_log()
    {
        using var dir = new TempDir();
        var log = Path.Combine(dir.Path, "log.jsonl");
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        runtime.GenerateFailures.Enqueue(new InvalidOperationException("claude -p exited 1: Received API Key = sk-short, Authorization: Bearer abc.def"));

        var r = await Runs.Orchestrator(runtime, dir.Path, new JsonlRunLog(log)).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        var json = JsonSerializer.Serialize(r, ContractJson.Options) + await File.ReadAllTextAsync(log);
        Assert.DoesNotContain("sk-short", json, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def", json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_that_throws_keeps_keys_out_of_the_result()
    {
        using var dir = new TempDir();
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        runtime.CreateFailures.Enqueue(new InvalidOperationException("claude -p exited 1: Received API Key = sk-short"));

        var r = await Runs.Orchestrator(runtime, dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl"))).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.DoesNotContain("sk-short", JsonSerializer.Serialize(r, ContractJson.Options), StringComparison.Ordinal);
        Assert.Equal("node failed: claude -p exited 1: Received API Key = [redacted]", r.Summary);
    }
}
