using System.Net;
using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

public class CredentialHealthTests
{
    private const string Revoked = "claude -p exited 1: Failed to authenticate. API Error: 401 OAuth access token has been revoked.";

    [Theory]
    [InlineData(Revoked, true)]
    [InlineData("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", true)]
    [InlineData("worker ended failed: claude exited 1: API Error: 529 Overloaded", false)]
    [InlineData("the test at line 401 failed", false)]
    [InlineData(null, false)]
    public void Only_a_rejected_credential_counts(string? error, bool rejected) => Assert.Equal(rejected, CredentialHealth.Rejected(error));

    [Fact]
    public void A_credential_pauses_and_resumes_once_each()
    {
        var health = new CredentialHealth();
        var at = DateTimeOffset.Parse("2026-10-18T19:13:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(health.Fail("server", "API Error: 529 Overloaded", at));
        Assert.True(health.Fail("server", Revoked, at));
        Assert.False(health.Fail("server", Revoked, at.AddMinutes(5)));
        Assert.Equal(at, health.Paused("server")!.Since);
        Assert.Null(health.Paused("alice"));
        Assert.True(health.Succeed("server"));
        Assert.False(health.Succeed("server"));
        Assert.Null(health.Paused("server"));
    }

    [Fact]
    public void A_run_uses_its_accounts_own_credential_or_the_servers()
    {
        AccountSettings[] accounts = [new("alice", Model: new AccountModel("ALICE_KEY")), new("bob", AllowServerCredential: true)];
        Assert.Equal("alice", CredentialHealth.KeyOf("alice", accounts));
        Assert.Equal(CredentialHealth.Server, CredentialHealth.KeyOf("bob", accounts));
        Assert.Equal(CredentialHealth.Server, CredentialHealth.KeyOf(null, accounts));
    }

    [Fact]
    public async Task A_run_on_a_revoked_token_pauses_admission_until_the_credential_answers_again()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        runtime.GenerateFailures.Enqueue(new InvalidOperationException(Revoked));
        await using var s = await TestServer.StartAsync(runtime);

        Assert.Equal(HttpStatusCode.OK, (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 30)).StatusCode);
        var refused = await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("credential_paused", error.GetProperty("code").GetString());
        Assert.Contains("revoked", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        await s.Service.ReportCredentialAsync(CredentialHealth.Server, null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 30)).StatusCode);
    }

    [Fact]
    public async Task A_probe_pauses_on_a_rejection_resumes_on_an_answer_and_notifies_each_change_once()
    {
        using var dir = new TempDir();
        List<string> posted = [];
        var runs = new RunService(Runs.Orchestrator(new ScriptedRuntime(Runs.DraftReply), dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl"))),
            Repo.Path("presets"), CancellationToken.None, notify: (title, _, _) => { posted.Add(title); return Task.CompletedTask; });
        var answers = new Queue<ModelProbe>([
            new(false, "m", 1, Revoked), new(false, "m", 1, Revoked), new(false, "m", 1, "no answer within 60 s"), new(true, "m", 1, null), new(true, "m", 1, null)]);
        var probe = new CredentialProbe(runs, (_, _) => Task.FromResult(answers.Dequeue()));

        for (var i = 0; i < 5; i++)
        {
            await probe.TryAsync(CredentialHealth.Server, CancellationToken.None);
            Assert.Equal(i < 3, runs.Credentials.Paused(CredentialHealth.Server) is not null);
        }
        Assert.Equal(["chargehand: credential 'server' rejected, runs paused", "chargehand: credential 'server' works again"], posted);
    }
}
