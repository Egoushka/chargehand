using System.Net;
using System.Text.Json;
using Chargehand.Containers;
using Chargehand.Driven;
using Chargehand.Runner;

namespace Chargehand.Tests;

/// <summary>ADR 0058: a runner that does not answer is told apart from one that refuses, because a laptop that sleeps must be waited for and a wrong key must not be.</summary>
public class RunnerClientOutageTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    private static RunnerClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null) =>
        new(new HttpClient(new Stub(respond)) { BaseAddress = new Uri("http://runner.example:4310"), Timeout = timeout ?? TimeSpan.FromSeconds(30) });

    private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body = """{"error":"nope"}""") =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    [Fact]
    public async Task A_connection_that_fails_is_an_unreachable_runner()
    {
        var client = Client((_, _) => throw new HttpRequestException("Connection refused"));

        var e = await Assert.ThrowsAsync<RunnerUnreachableException>(() => client.InspectAsync("c1", default));

        Assert.Contains("could not be reached", e.Message);
        Assert.Equal(Chargehand.Contracts.ErrorCode.ContainerUnavailable, e.Code);
    }

    [Fact]
    public async Task A_call_that_outlasts_the_clients_timeout_is_an_unreachable_runner_and_not_a_cancel()
    {
        var client = Client(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }, TimeSpan.FromMilliseconds(50));

        // A TaskCanceledException here would read as a cancel and stop a session whose runner merely did not answer.
        await Assert.ThrowsAsync<RunnerUnreachableException>(() => client.InspectAsync("c1", default));
    }

    [Fact]
    public async Task A_cancel_by_the_caller_stays_a_cancel()
    {
        var client = Client(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InspectAsync("c1", cancel.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_gateway_error_or_a_full_runner_can_be_waited_for(HttpStatusCode status)
    {
        var client = Client((_, _) => Reply(status));

        await Assert.ThrowsAsync<RunnerUnreachableException>(() => client.InspectAsync("c1", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_refusal_is_a_plain_error_that_is_not_waited_out(HttpStatusCode status)
    {
        var client = Client((_, _) => Reply(status));

        var e = await Assert.ThrowsAsync<ChargehandException>(() => client.InspectAsync("c1", default));

        Assert.IsNotType<RunnerUnreachableException>(e);
        Assert.Contains($"{(int)status}", e.Message);
    }

    [Fact]
    public async Task The_host_snapshot_leaves_out_a_state_while_the_runner_is_ready_so_an_older_server_still_reads_it()
    {
        var ready = JsonSerializer.Serialize(new HostSnapshot(4000, 16000, 0, 0, 0.2, 8), RunnerJson.Options);
        var draining = JsonSerializer.Serialize(new HostSnapshot(4000, 16000, 0, 0, 0.2, 8, State: HostSnapshot.Draining, StateReason: "on battery"), RunnerJson.Options);

        Assert.DoesNotContain("state", ready, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"draining\"", draining);
        Assert.Contains("\"state_reason\":\"on battery\"", draining);
        Assert.DoesNotContain("is_draining", draining, StringComparison.Ordinal);
        Assert.True(JsonSerializer.Deserialize<HostSnapshot>(draining, RunnerJson.Options)!.IsDraining);

        var client = Client((_, _) => Reply(HttpStatusCode.OK, draining));
        Assert.Equal("on battery", (await client.ReadAsync(default))!.StateReason);
    }

    [Fact]
    public async Task A_runner_older_than_the_host_route_says_unknown()
    {
        var client = Client((_, _) => Reply(HttpStatusCode.NotFound));

        Assert.Null(await client.ReadAsync(default));
    }
}
