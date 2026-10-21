using System.Net;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Forge.Tests;

/// <summary>CHARGEHAND-244: a required check lives in an active ruleset or in classic branch protection; either one gates a merge. A refusal or an odd body is <see cref="ChecksGate.Unknown"/>.</summary>
public class BranchRulesTests
{
    private sealed class Pages(HttpStatusCode status, string rules, string branch) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(url.Contains("/rules/branches/", StringComparison.Ordinal) ? rules : branch) });
        }
    }

    private static Task<ChecksGate> Read(Pages pages) =>
        new GitHubPullRequests(new HttpClient(pages) { BaseAddress = new Uri("https://api.example.test/") }, "the-token").ChecksGateAsync(new RemoteRepository("o", "r"), "main", default);

    private const string NoProtection = """{"name":"main","protected":false}""";

    [Fact]
    public async Task A_ruleset_that_requires_a_check_gates_the_branch()
    {
        var pages = new Pages(HttpStatusCode.OK, """[{"type":"deletion"},{"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"ci-gate"}]}}]""", NoProtection);

        Assert.Equal(ChecksGate.Required, await Read(pages));
        Assert.Equal(["https://api.example.test/repos/o/r/rules/branches/main", "https://api.example.test/repos/o/r/branches/main"], pages.Urls);
    }

    [Fact]
    public async Task Classic_protection_that_enforces_a_check_gates_the_branch() =>
        Assert.Equal(ChecksGate.Required, await Read(new Pages(HttpStatusCode.OK, "[]",
            """{"protected":true,"protection":{"required_status_checks":{"enforcement_level":"everyone","contexts":["build"],"checks":[{"context":"build"}]}}}""")));

    [Theory]
    [InlineData("[]", NoProtection)]
    [InlineData("""[{"type":"pull_request"},{"type":"required_status_checks","parameters":{"required_status_checks":[]}}]""", NoProtection)]
    [InlineData("[]", """{"protected":true,"protection":{"required_status_checks":{"enforcement_level":"off","contexts":["build"]}}}""")]
    public async Task A_branch_without_an_enforced_check_is_not_gated(string rules, string branch) =>
        Assert.Equal(ChecksGate.None, await Read(new Pages(HttpStatusCode.OK, rules, branch)));

    [Fact]
    public async Task A_refusal_or_a_body_that_is_not_rules_is_unknown()
    {
        Assert.Equal(ChecksGate.Unknown, await Read(new Pages(HttpStatusCode.NotFound, "{}", "{}")));
        Assert.Equal(ChecksGate.Unknown, await Read(new Pages(HttpStatusCode.OK, "not json", NoProtection)));
    }
}
