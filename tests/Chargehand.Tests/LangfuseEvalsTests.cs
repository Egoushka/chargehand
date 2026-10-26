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
    public async Task A_dataset_that_does_not_exist_is_an_error_naming_it()
    {
        // A renamed dataset, a wrong base URL or a key for another project all answer 404 to the dataset lookup. That
        // must not read as an unseeded (empty) dataset, or the gate passes without checking anything.
        var h = new FakeHandler(_ => (HttpStatusCode.NotFound, """{"message":"Dataset not found"}"""));

        var e = await Assert.ThrowsAsync<DatasetNotFoundException>(() => Make(h).ItemsAsync("chargehand-review-worker", CancellationToken.None));

        Assert.Contains("chargehand-review-worker", e.Message, StringComparison.Ordinal);
        Assert.Contains("not found", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Seen, p => p.Contains("dataset-items", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_existing_dataset_without_items_is_empty()
    {
        var h = new FakeHandler(path => path.StartsWith("/api/public/v2/datasets/", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"name":"chargehand-review-worker"}""")
            : (HttpStatusCode.OK, """{"data":[],"meta":{"totalPages":0}}"""));

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
