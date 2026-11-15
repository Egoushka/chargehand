using Chargehand.Automation;
using Chargehand.Contracts;

namespace Chargehand.Tests;

public sealed class EpicSplitterTests
{
    private const string Repo = "owner/app";

    private readonly FakeGitHub _github = new();
    private readonly FakeRunLog _log = new();
    private readonly ManualTimeProvider _time = new();

    /// <summary>Starts like the rig's starter, and ends the run at once with the answer.</summary>
    private sealed class AnsweringStarter(FakeStarter inner, FakeRunLog log, string answer) : IBatchStarter
    {
        public Task<BatchStart> StartAsync(RunRequest request, CancellationToken ct) => StartAsync(request, false, ct);

        public async Task<BatchStart> StartAsync(RunRequest request, bool budgetIsCap, CancellationToken ct)
        {
            var started = await inner.StartAsync(request, budgetIsCap, ct);
            log.Finish(started.RunId!, ResultStatus.Completed, answer);
            return started;
        }
    }

    private static IntakeIssue Epic(string body = "Do the big thing.") => new(7, "Big thing", body, "alice", false, ["agent:epic"], DateTimeOffset.UtcNow);

    private static string Answer(int count, Func<int, string>? body = null) =>
        "{\"issues\":[" + string.Join(',', Enumerable.Range(1, count).Select(i => $"{{\"title\":\"Part {i}\",\"body\":\"{(body ?? (_ => "Do part."))(i)}\"}}")) + "]}";

    private (EpicSplitter Splitter, FakeStarter Starter) Splitter(string answer)
    {
        var starter = new FakeStarter(_log);
        return (new EpicSplitter(_github, new AnsweringStarter(starter, _log, answer), _log, (_, _) => Task.FromResult(new RepositoryRef("/repo", "abc1234")), _time, _ => { }), starter);
    }

    [Fact]
    public async Task Three_sub_issues_file_three_and_edit_the_parent_once()
    {
        var (splitter, starter) = Splitter(Answer(3));

        var filed = await splitter.SplitAsync(Repo, Epic(), default);

        Assert.Equal(3, filed);
        Assert.Equal(3, _github.CreatedIssues.Count);
        var edit = Assert.Single(_github.EditedBodies);
        Assert.Equal(7, edit.Number);
        Assert.Contains("Do the big thing.\n\n<!-- chargehand-split -->\n- [ ] #5000\n- [ ] #5001\n- [ ] #5002", edit.Body, StringComparison.Ordinal);
        Assert.Contains((Repo, 7, SweepLabels.Blocked), _github.LabelAdds);
        Assert.Equal("split", Assert.Single(starter.Started).Context.Preset);
    }

    [Fact]
    public async Task A_credential_in_a_sub_issue_files_nothing()
    {
        var (splitter, _) = Splitter(Answer(2, i => i == 2 ? "key " + "AKIA" + "IOSFODNN7EXAMPLE" : "fine"));

        Assert.Equal(0, await splitter.SplitAsync(Repo, Epic(), default));

        Assert.Empty(_github.CreatedIssues);
        Assert.Empty(_github.EditedBodies);
        Assert.Contains("<!-- chargehand-split -->", Assert.Single(_github.Posted).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parent_with_the_split_marker_is_not_split_again()
    {
        var (splitter, starter) = Splitter(Answer(3));

        Assert.Equal(0, await splitter.SplitAsync(Repo, Epic("Body\n\n<!-- chargehand-split -->\n- [ ] #9"), default));

        Assert.Empty(starter.Started);
        Assert.Empty(_github.CreatedIssues);
    }

    [Fact]
    public async Task Eleven_sub_issues_file_nothing()
    {
        var (splitter, _) = Splitter(Answer(11));

        Assert.Equal(0, await splitter.SplitAsync(Repo, Epic(), default));

        Assert.Empty(_github.CreatedIssues);
        Assert.Single(_github.Posted);
    }

    [Fact]
    public async Task Unparsable_answer_files_nothing_and_refuses_once()
    {
        var (splitter, _) = Splitter("I would split it into three parts.");

        await splitter.SplitAsync(Repo, Epic(), default);
        await splitter.SplitAsync(Repo, Epic(), default);

        Assert.Empty(_github.CreatedIssues);
        Assert.Empty(_github.EditedBodies);
        Assert.Single(_github.Posted);
    }
}
