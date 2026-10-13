using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0051: the server runs intake while it runs, and a trigger becomes an ordinary run of that server.</summary>
public class IntakeServerTests
{
    [Fact]
    public async Task The_server_polls_at_start_and_a_label_becomes_a_run_in_its_log_and_stopping_the_server_stops_intake()
    {
        using var root = new TempDir();
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var github = new FakeGitHub();
        github.Issues.Add(IntakeRig.Issue(7, labels: ["chargehand"]));
        github.LabelEvents[7] = new LabelEvent(1, "alice");
        var settings = new AutomationGitHub(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, ["alice"]);
        var intake = new GitHubIntake(settings, github, log, new FakeHeads(), IntakeState.Load(Path.Combine(root.Path, "intake.json")));
        var app = ChargehandServer.Create(new ServerSettings(0, TestServer.Key, Repo.Path("presets")), Runs.Orchestrator(new ScriptedRuntime(Runs.DraftReply), root.Path, log), log, intake: intake);
        try
        {
            await app.StartAsync();
            for (var i = 0; i < 200 && github.Posted.Count == 0; i++)
                await Task.Delay(50);

            var comment = Assert.Single(github.Posted).Body;
            var run = comment.Split('`')[1];
            // The comment names the run when it is accepted; its start record is written a moment later by the run itself.
            StartRecord? start = null;
            for (var i = 0; i < 200 && (start = (await log.ReadAsync(run, default)).Start) is null; i++)
                await Task.Delay(50);
            Assert.NotNull(start);
            Assert.Equal("driven", start.Request.Context.Preset);
            Assert.Equal("issue-7", Assert.Single(start.Request.Driven!.Tasks).Id);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_halted_server_refuses_the_batch_and_intake_tries_again_later()
    {
        using var root = new TempDir();
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var app = ChargehandServer.Create(new ServerSettings(0, TestServer.Key, Repo.Path("presets")), Runs.Orchestrator(new ScriptedRuntime(Runs.DraftReply), root.Path, log), log);
        try
        {
            await app.StartAsync();
            var runs = (RunService)app.Services.GetService(typeof(RunService))!;
            runs.Halt();
            var start = await new RunServiceStarter(runs).StartAsync(Runs.DraftRequest(), default);

            Assert.Null(start.RunId);
            Assert.Equal(RunService.HaltedMessage, start.Refusal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
