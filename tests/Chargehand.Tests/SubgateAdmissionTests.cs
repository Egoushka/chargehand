using System.Net;
using System.Text;
using Chargehand.Automation;

namespace Chargehand.Tests;

public class SubgateAdmissionTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static SubgateAdmission Admission(Stub stub, ManualTimeProvider time, List<string>? warnings = null) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://subgate.test/") }, "chargehand", time, warnings is null ? null : warnings.Add);

    [Fact]
    public async Task Allow_means_no_hold()
    {
        var admission = Admission(new Stub(_ => Json("""{"decision":"allow","would":"allow"}""")), new ManualTimeProvider());
        Assert.Null(await admission.HoldUntilAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Hold_is_remembered_until_the_retry_so_a_queue_asks_once()
    {
        var time = new ManualTimeProvider();
        var stub = new Stub(_ => Json("""{"decision":"hold","would":"hold","retry_after_s":600}"""));
        var admission = Admission(stub, time);
        var first = await admission.HoldUntilAsync(CancellationToken.None);

        Assert.Equal(time.GetUtcNow().AddSeconds(600), first);
        Assert.Equal(first, await admission.HoldUntilAsync(CancellationToken.None));
        Assert.Equal(1, stub.Calls);

        time.Advance(TimeSpan.FromSeconds(601));
        await admission.HoldUntilAsync(CancellationToken.None);
        Assert.Equal(2, stub.Calls);
    }

    [Theory]
    [InlineData(500, "{}")]
    [InlineData(401, """{"error":"no"}""")]
    [InlineData(200, "not json")]
    [InlineData(200, """{"decision":"surprise"}""")]
    public async Task An_unusable_answer_fails_open_and_warns_once(int code, string body)
    {
        var warnings = new List<string>();
        var admission = Admission(new Stub(_ => Json(body, (HttpStatusCode)code)), new ManualTimeProvider(), warnings);

        Assert.Null(await admission.HoldUntilAsync(CancellationToken.None));
        Assert.Null(await admission.HoldUntilAsync(CancellationToken.None));
        Assert.Single(warnings);
    }

    [Fact]
    public async Task An_unreachable_subgate_fails_open()
    {
        var admission = Admission(new Stub(_ => throw new HttpRequestException("refused")), new ManualTimeProvider());
        Assert.Null(await admission.HoldUntilAsync(CancellationToken.None));
    }
}
