using System.Net;
using System.Text;
using Chargehand.Evals;

namespace Chargehand.Tests;

/// <summary>ADR 0019: eval items live in Langfuse datasets, read over the public API.</summary>
public class LangfuseEvalsTests
{
    private sealed class FakeHandler(Func<string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            Seen.Add(path);
            var (status, json) = respond(path);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private const string Item = """
        {"id":"item-1","status":"ACTIVE","input":{"contract_version":"request/v1","text":"Where is X?","context":{"interactive":false,"preset":"review"}},
         "expectedOutput":{"reference_files":["src/x.cs"]},"metadata":null}
        """;

    private static LangfuseEvals Make(FakeHandler h) => new(new HttpClient(h) { BaseAddress = new Uri("http://127.0.0.1:3000/") }, "pk", "sk");

    [Fact]
    public async Task A_dataset_that_does_not_exist_has_no_items()
    {
        // Langfuse answers 404 for the items of a dataset nobody created yet: a new cell's, before its first push.
        var h = new FakeHandler(_ => (HttpStatusCode.NotFound, """{"message":"Dataset not found"}"""));

        var items = await Make(h).ItemsAsync("chargehand-review-worker", CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task An_existing_dataset_returns_its_active_items()
    {
        var h = new FakeHandler(path => path.StartsWith("/api/public/v2/datasets/", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"name":"chargehand-review-worker"}""")
            : (HttpStatusCode.OK, $$$"""{"data":[{{{Item}}}],"meta":{"totalPages":1}}"""));

        var items = await Make(h).ItemsAsync("chargehand-review-worker", CancellationToken.None);

        Assert.Equal("item-1", Assert.Single(items).Id);
    }

    [Fact]
    public async Task Any_other_failure_still_throws()
    {
        var h = new FakeHandler(_ => (HttpStatusCode.Unauthorized, """{"message":"Invalid credentials"}"""));

        await Assert.ThrowsAsync<HttpRequestException>(() => Make(h).ItemsAsync("chargehand-review-worker", CancellationToken.None));
    }
}
