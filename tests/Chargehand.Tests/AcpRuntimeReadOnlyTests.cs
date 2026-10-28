using Chargehand.Runtime;
using static Chargehand.Tests.AcpTestSupport;

namespace Chargehand.Tests;

/// <summary>ADR 0086, decision 8: a research worker on an ACP agent is read-only three ways, and the run records what it refused.</summary>
public class AcpRuntimeReadOnlyTests
{
    [Theory]
    [InlineData("edit")]
    [InlineData("execute")]
    [InlineData("delete")]
    public async Task An_edit_or_execute_request_is_rejected_and_the_run_records_it(string kind)
    {
        await using var agent = new AcpFixture();

        var (_, messages, outcome) = await agent.Turn($"ask {kind}");

        Assert.Equal(IdleOutcome.Succeeded, outcome);
        Assert.Contains("permission:selected:reject", messages.First(m => m.Kind == WorkerMessageKind.Assistant).Text, StringComparison.Ordinal);
        var refusal = messages.Single(m => m.Kind == WorkerMessageKind.Other);
        Assert.Contains("rejected, read-only worker", refusal.Text, StringComparison.Ordinal);
        Assert.Contains(kind, refusal.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_request_is_allowed_once_and_leaves_no_refusal()
    {
        await using var agent = new AcpFixture();

        var (_, messages, _) = await agent.Turn("ask read");

        Assert.Contains("permission:selected:allow-once", messages.First(m => m.Kind == WorkerMessageKind.Assistant).Text, StringComparison.Ordinal);
        Assert.DoesNotContain(messages, m => m.Kind == WorkerMessageKind.Other);
    }

    [Fact]
    public async Task The_agents_plan_mode_is_set_when_it_has_one()
    {
        await using var agent = new AcpFixture();

        var (id, _, _) = await agent.Turn("say hello");

        Assert.Equal("plan", agent.Runtime.ReadOnlyMode(id));
    }

    [Fact]
    public async Task The_process_starts_in_the_sandbox_with_a_private_directory_the_key_and_a_network_for_the_model()
    {
        var sandbox = new RecordingSandbox();
        await using var agent = new AcpFixture(sandbox: sandbox);

        await agent.Turn("say hello");

        var spec = Assert.Single(sandbox.Wrapped);
        Assert.True(spec.Network);
        Assert.False(spec.WorkDirectory.StartsWith(agent.Dir.Path, StringComparison.Ordinal));
        Assert.Equal("k-test", spec.Env!["FAKE_API_KEY"]);
        Assert.Empty(spec.PassEnv);
    }

    [Fact]
    public async Task A_research_worker_never_has_a_permission_to_answer()
    {
        await using var agent = new AcpFixture();

        var (id, _, _) = await agent.Turn("ask execute");

        Assert.Empty(await agent.Runtime.PendingPermissionsAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Runtime.AnswerPermissionAsync(id, "x", PermissionDecision.Once, null, default));
    }

    [Fact]
    public void The_runtime_says_it_is_for_research_presets_only_and_on_an_api_key()
    {
        var runtime = NewRuntime();

        Assert.True(runtime.ResearchOnly);
        Assert.Equal("api_key", runtime.Credential);
    }

    [Fact]
    public async Task Granted_services_are_reported_unavailable_because_an_agent_cannot_be_limited_to_their_granted_tools()
    {
        await using var agent = new AcpFixture();
        var grant = new ServiceGrant("memory", new HttpServiceTransport(new Uri("http://127.0.0.1:1/mcp"), new Dictionary<string, string>()), ["recall"], new string('a', 64));
        var session = await agent.Runtime.CreateAsync(Spec(agent.Dir.Path, grant), default);

        Assert.Contains("memory", agent.Runtime.UnavailableServices(session.Id).Keys);
    }

    [Fact]
    public async Task An_edit_that_slips_past_every_guard_shows_in_the_diff()
    {
        await using var agent = new AcpFixture();
        Git(agent.Dir.Path, "init", "-q");
        Git(agent.Dir.Path, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "base");
        var session = await agent.Runtime.CreateAsync(Spec(agent.Dir.Path), default);
        File.WriteAllText(Path.Combine(agent.Dir.Path, "new.txt"), "x\n");

        var diff = await agent.Runtime.DiffAsync(session.Id, default);

        Assert.Equal("new.txt", Assert.Single(diff).File);
    }

    private static void Git(string directory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
