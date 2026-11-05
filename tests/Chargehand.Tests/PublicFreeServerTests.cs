using System.Net;
using System.Net.Http.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>The server side of the free-model pool (ADR 0093): admission, the owner's switch, and the approval gate that has nothing to hold for a free run.</summary>
public class PublicFreeServerTests
{
    private const string Forge = "owner/open";

    private sealed class Rig(TestServer server, RepositoryRef repo, PublicFreeSwitch on) : IAsyncDisposable
    {
        public TestServer Server => server;

        public RepositoryRef Repo => repo;

        public PublicFreeSwitch Switch => on;

        public RunRequest Request(string preset, decimal? budget = null)
        {
            var request = Runs.CheapRequest(repo);
            return request with { Context = request.Context with { Preset = preset, BudgetUsd = budget } };
        }

        public Task<(RunRequest Request, AccountRefusal? Refusal)> AdmitAsync(string preset) => server.Service.AdmitAsync(new Principal("owner"), Request(preset), CancellationToken.None);

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(RepositoryVisibility visibility, bool enabled = true, bool underWork = false)
    {
        RepositoryRef? repo = null;
        var on = new PublicFreeSwitch(null);
        on.Set(enabled);
        var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), publicFree: root =>
        {
            repo = Runs.GitRepo(underWork ? Directory.CreateDirectory(Path.Combine(root, "work")).FullName : root);
            Runs.SetOrigin(repo, $"https://github.com/{Forge}.git");
            var profile = Runs.Profile(root) with
            {
                Automation = new AutomationSettings(new AutomationGitHub(new Dictionary<string, AutomationRepository> { [Forge] = new(repo.Path) }, ["octo"])),
                PublicFree = PublicFreePoolTests.Settings,
            };
            return new PublicFreeGate(profile, new PublicFreePool(PublicFreePoolTests.Settings, new PublicFreePoolTests.FakeVisibility(_ => visibility), on, TimeProvider.System),
                () => new ScriptedRuntime(Runs.DraftReply));
        });
        return new Rig(server, repo!, on);
    }

    [Theory]
    [InlineData(nameof(RepositoryVisibility.Private))]
    [InlineData(nameof(RepositoryVisibility.Internal))]
    [InlineData(nameof(RepositoryVisibility.Unknown))]
    public async Task APaidPresetOnANonPublicRepositoryStaysOnTheServersPreset(string visibility)
    {
        await using var rig = await StartAsync(Enum.Parse<RepositoryVisibility>(visibility));

        var (admitted, refusal) = await rig.AdmitAsync("cheap");

        Assert.Null(refusal);
        Assert.Equal("cheap", admitted.Context.Preset);
    }

    [Fact]
    public async Task APublicRepositoryMovesAReplacedPresetAndOnlyThat()
    {
        await using var rig = await StartAsync(RepositoryVisibility.Public);

        Assert.Equal("public-free", (await rig.AdmitAsync("cheap")).Request.Context.Preset);
        Assert.Equal("code", (await rig.AdmitAsync("code")).Request.Context.Preset);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("off")]
    public async Task NamingThePoolPresetWhereItMayNotRunIsRefusedAndNeverRun(string why)
    {
        await using var rig = await StartAsync(why == "private" ? RepositoryVisibility.Private : RepositoryVisibility.Public, enabled: why != "off");

        var (_, refusal) = await rig.AdmitAsync("public-free");

        Assert.NotNull(refusal);
        Assert.Equal("public_free_refused", refusal.Code);
        Assert.Equal(StatusCodes403, refusal.Status);
    }

    private const int StatusCodes403 = 403;

    [Fact]
    public async Task WithTheSwitchOffOrUnderWorkAPublicRepositoryKeepsItsPreset()
    {
        await using (var off = await StartAsync(RepositoryVisibility.Public, enabled: false))
            Assert.Equal("cheap", (await off.AdmitAsync("cheap")).Request.Context.Preset);
        await using (var work = await StartAsync(RepositoryVisibility.Public, underWork: true))
            Assert.Equal("cheap", (await work.AdmitAsync("cheap")).Request.Context.Preset);
    }

    [Fact]
    public async Task APoolRunIsNotHeldForPaidSpendApprovalButTheSameRunOnAPrivateRepositoryIs()
    {
        await using var open = await StartAsync(RepositoryVisibility.Public);
        Assert.NotEqual(HttpStatusCode.Conflict, (await open.Server.PostAsync(open.Request("cheap", 8m), 0)).StatusCode);

        await using var closed = await StartAsync(RepositoryVisibility.Private);
        Assert.Equal(HttpStatusCode.Conflict, (await closed.Server.PostAsync(closed.Request("cheap", 8m), 0)).StatusCode);
    }

    [Fact]
    public async Task TheSwitchIsOffByDefaultIsKeptAndAnswersTheBody()
    {
        await using var rig = await StartAsync(RepositoryVisibility.Public, enabled: false);

        var before = await rig.Server.Http.GetFromJsonAsync<PublicFreeBody>("/v1/limits/public-free", ContractJson.Options);
        Assert.Equal(new PublicFreeBody("public-free/v1", false, true, "public-free"), before);

        var put = await rig.Server.Http.PutAsJsonAsync("/v1/limits/public-free", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await put.Content.ReadFromJsonAsync<PublicFreeBody>(ContractJson.Options))!.Enabled);
        Assert.True(rig.Switch.Enabled);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"enabled":"yes"}""")]
    [InlineData("""{"enabled":null}""")]
    [InlineData("[true]")]
    [InlineData("not json")]
    public async Task ABodyThatIsNotAnExplicitBooleanIsRefusedAndChangesNothing(string body)
    {
        await using var rig = await StartAsync(RepositoryVisibility.Public, enabled: false);

        var put = await rig.Server.Http.PutAsync("/v1/limits/public-free", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.False(rig.Switch.Enabled);
    }

    [Fact]
    public async Task WithoutAPublicFreeBlockThereIsNothingToSwitch()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));

        Assert.Equal(HttpStatusCode.Conflict, (await server.Http.PutAsJsonAsync("/v1/limits/public-free", new { enabled = true })).StatusCode);
        var body = await server.Http.GetFromJsonAsync<PublicFreeBody>("/v1/limits/public-free", ContractJson.Options);
        Assert.False(body!.Configured);
        Assert.False(body.Enabled);
    }

    [Fact]
    public async Task OnlyTheOwnersKeyReadsOrChangesTheSwitch()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [new AccountSettings("alice", AllowServerCredential: true)]);
        using var alice = server.As(server.Keys.Create("alice", null).Key);
        using var nobody = new HttpClient { BaseAddress = server.BaseAddress };

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/v1/limits/public-free")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/v1/limits/public-free", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.GetAsync("/v1/limits/public-free")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.PutAsJsonAsync("/v1/limits/public-free", new { enabled = true })).StatusCode);
    }
}
