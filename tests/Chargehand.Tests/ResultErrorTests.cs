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
}
