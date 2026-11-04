using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;

namespace Chargehand.Tests;

/// <summary>The pool's key reaches a run only through the visibility answer, and an empty pool never falls back to a paid runtime (ADR 0093).</summary>
public class PublicFreeRuntimeTests
{
    private const string Forge = "owner/open";

    private sealed record Rig(Orchestrator Orchestrator, ScriptedRuntime Server, ScriptedRuntime Pool, RepositoryRef Repo, RunRequest Request);

    private static Rig Build(TempDir root, IRepositoryVisibility visibility, string preset, string? origin = $"https://github.com/{Forge}.git", bool enabled = true, bool mapped = true, bool underWork = false, MemoryStack? memory = null)
    {
        var repo = Runs.GitRepo(underWork ? Directory.CreateDirectory(Path.Combine(root.Path, "work")).FullName : root.Path);
        if (origin is not null)
            Runs.SetOrigin(repo, origin);
        var settings = PublicFreePoolTests.Settings;
        var profile = Runs.Profile(root.Path) with
        {
            Models = new Dictionary<string, string> { ["provider/small-model"] = "p/small", ["provider/free-model"] = "p/small" },
            Automation = mapped ? new AutomationSettings(new AutomationGitHub(new Dictionary<string, AutomationRepository> { [Forge] = new(repo.Path) }, ["octo"])) : null,
            PublicFree = settings,
        };
        var server = new ScriptedRuntime(Runs.WorkerReply);
        var pool = new ScriptedRuntime(Runs.WorkerReply);
        var on = new PublicFreeSwitch(null);
        on.Set(enabled);
        var gate = new PublicFreeGate(profile, new PublicFreePool(settings, visibility, on, TimeProvider.System), () => pool);
        var orchestrator = new Orchestrator(profile, server, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>(), memory, publicFree: gate);
        var request = Runs.CheapRequest(repo);
        return new(orchestrator, server, pool, repo, request with { Context = request.Context with { Preset = preset } });
    }

    [Theory]
    [InlineData(nameof(RepositoryVisibility.Private))]
    [InlineData(nameof(RepositoryVisibility.Internal))]
    [InlineData(nameof(RepositoryVisibility.Unknown))]
    public async Task TheKeyDoesNotReachAPrivateRepository(string visibility)
    {
        using var root = new TempDir();
        var rig = Build(root, new PublicFreePoolTests.FakeVisibility(_ => Enum.Parse<RepositoryVisibility>(visibility)), "public-free");

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.RepositoryNotAllowed, result.Error?.Code);
        Assert.Empty(rig.Pool.Created);
        Assert.Empty(rig.Server.Created);
    }

    [Theory]
    [InlineData("https://github.com/owner/private-thing.git")]
    [InlineData("git@github.com:someone/else.git")]
    [InlineData("https://example.test/owner/open.git")]
    [InlineData(null)]
    public async Task AMappedNameThatTheCheckoutsOriginDoesNotMatchNeverGetsThePool(string? origin)
    {
        using var root = new TempDir();
        var rig = Build(root, new PublicFreePoolTests.FakeVisibility(_ => RepositoryVisibility.Public), "public-free", origin);

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ErrorCode.RepositoryNotAllowed, result.Error?.Code);
        Assert.Empty(rig.Pool.Created);
        Assert.Empty(rig.Server.Created);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("unmapped")]
    [InlineData("work")]
    public async Task APublicAnswerIsNotEnoughWhenTheSwitchIsOffTheCheckoutIsUnmappedOrUnderWork(string why)
    {
        using var root = new TempDir();
        var rig = Build(root, new PublicFreePoolTests.FakeVisibility(_ => RepositoryVisibility.Public), "public-free", enabled: why != "off", mapped: why != "unmapped", underWork: why == "work");

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ErrorCode.RepositoryNotAllowed, result.Error?.Code);
        Assert.Empty(rig.Pool.Created);
        Assert.Empty(rig.Server.Created);
    }

    [Fact]
    public async Task APoolRunNeitherRecallsFromMemoryNorRetainsIntoIt()
    {
        using var root = new TempDir();
        var fake = new MemoryStackTests.Fake(_ => [new RecalledMemory("1", "A fact retained from a private repository.")]);
        var stack = new MemoryStack([new MemorySource("shared", fake, new MemoryScope("shared", "ns"), new MemoryLimits(), true)]);
        var rig = Build(root, new PublicFreePoolTests.FakeVisibility(_ => RepositoryVisibility.Public), "public-free", memory: stack);

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Single(rig.Pool.Created);
        Assert.DoesNotContain("private repository", string.Join("\n", rig.Pool.Prompts), StringComparison.Ordinal);
        Assert.Empty(fake.Retained);
        Assert.True(result.Status != ResultStatus.Failed, $"{result.Error?.Code}: {result.Summary}");
    }

    [Fact]
    public void TheCredentialAndAMissingSecretNeverShowTheKey()
    {
        var credential = new Chargehand.ClaudeCode.ClaudeCodeCredential("sk-secret-value", Subscription: false);
        Assert.DoesNotContain("sk-secret-value", credential.ToString());
        Assert.DoesNotContain("sk-secret-value", $"{credential}");

        var error = Assert.Throws<InvalidOperationException>(() => Runs.Profile(Path.GetTempPath()).Secret("chargehand-public-free-key-not-set"));
        Assert.Contains("chargehand-public-free-key-not-set", error.Message);
    }

    [Fact]
    public async Task AnOriginThatNamesTheSameRepositoryInAnotherCaseOrFormStillQualifies()
    {
        using var root = new TempDir();
        var rig = Build(root, new PublicFreePoolTests.FakeVisibility(_ => RepositoryVisibility.Public), "public-free", "git@github.com:Owner/Open.git");

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.True(result.Status != ResultStatus.Failed, $"{result.Error?.Code}: {result.Summary}");
        Assert.Single(rig.Pool.Created);
    }

    [Fact]
    public async Task AnErroredLookupDoesNotReachThePoolEither()
    {
        using var root = new TempDir();
        var rig = Build(root, new PublicFreePoolTests.ThrowingVisibility(new HttpRequestException("offline")), "public-free");

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ErrorCode.RepositoryNotAllowed, result.Error?.Code);
        Assert.Empty(rig.Pool.Created);
        Assert.Empty(rig.Server.Created);
    }

    [Fact]
    public async Task APublicRepositoryRunsOnThePoolAndNotOnTheServer()
    {
        using var root = new TempDir();
        var rig = Build(root, new PublicFreePoolTests.FakeVisibility(_ => RepositoryVisibility.Public), "public-free");

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.True(result.Status != ResultStatus.Failed, $"{result.Error?.Code}: {result.Summary}");
        Assert.Single(rig.Pool.Created);
        Assert.Empty(rig.Server.Created);
    }

    [Fact]
    public async Task ExhaustedPoolFailsWithoutFallback()
    {
        using var root = new TempDir();
        var rig = Build(root, new PublicFreePoolTests.FakeVisibility(_ => RepositoryVisibility.Public), "public-free");
        for (var i = 0; i < 8; i++)
        {
            rig.Pool.GenerateFailures.Enqueue(new InvalidOperationException("429 rate_limit_error: free quota exhausted"));
            rig.Pool.CreateFailures.Enqueue(new InvalidOperationException("429 rate_limit_error: free quota exhausted"));
        }

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.RateLimited, result.Error?.Code);
        Assert.Empty(rig.Server.Created);
        Assert.Empty(rig.Server.Prompts);
        Assert.Empty(rig.Server.IntakePrompts);
    }
}
