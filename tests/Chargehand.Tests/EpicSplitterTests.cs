using Chargehand.Automation;
using Chargehand.Contracts;

namespace Chargehand.Tests;

public sealed class EpicSplitterTests : IDisposable
{
    private const string Repo = "owner/app";

    private readonly TempDir _dir = new();
    private readonly FakeGitHub _github = new();
    private readonly FakeRunLog _log = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeStarter _starter;
    private readonly EpicSplitter _splitter;

    public EpicSplitterTests()
    {
        _starter = new FakeStarter(_log);
        _splitter = new EpicSplitter(_github, _starter, _log, (_, _) => Task.FromResult(new RepositoryRef("/repo", "abc1234")), Path.Combine(_dir.Path, "split.json"), _time, _ => { });
    }

    public void Dispose() => _dir.Dispose();

    private IntakeIssue Epic(string body = "Do the big thing.")
    {
        var epic = new IntakeIssue(7, "Big thing", body, "alice", false, ["agent:epic"], DateTimeOffset.UtcNow);
        _github.Issues.RemoveAll(i => i.Number == 7);
        _github.Issues.Add(epic);
        return epic;
    }

    private static string Answer(int count, Func<int, string>? body = null) =>
        "{\"issues\":[" + string.Join(',', Enumerable.Range(1, count).Select(i => $"{{\"title\":\"Part {i}\",\"body\":\"{(body ?? (_ => "Do part."))(i)}\"}}")) + "]}";

    /// <summary>The first call starts the run, it ends with the answer, the second call reads it.</summary>
    private async Task<int> Split(string answer, IntakeIssue? epic = null)
    {
        epic ??= Epic();
        await _splitter.SplitAsync(Repo, epic, default);
        _log.Finish($"run-{_starter.Started.Count}", ResultStatus.Completed, answer);
        return await _splitter.SplitAsync(Repo, epic, default);
    }

    [Fact]
    public async Task Three_sub_issues_file_three_and_edit_the_parent_once()
    {
        Assert.Equal(3, await Split(Answer(3)));

        Assert.Equal(3, _github.CreatedIssues.Count);
        var edit = Assert.Single(_github.EditedBodies);
        Assert.Equal(7, edit.Number);
        Assert.Contains("Do the big thing.\n\n<!-- chargehand-split -->\n- [ ] #5000\n- [ ] #5001\n- [ ] #5002", edit.Body, StringComparison.Ordinal);
        Assert.Contains((Repo, 7, SweepLabels.Blocked), _github.LabelAdds);
        Assert.Equal("split", Assert.Single(_starter.Started).Context.Preset);
    }

    [Fact]
    public async Task A_credential_in_a_sub_issue_files_nothing()
    {
        Assert.Equal(0, await Split(Answer(2, i => i == 2 ? "key " + "AKIA" + "IOSFODNN7EXAMPLE" : "fine")));

        Assert.Empty(_github.CreatedIssues);
        Assert.Empty(_github.EditedBodies);
        Assert.Contains("<!-- chargehand-split -->", Assert.Single(_github.Posted).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parent_with_the_split_marker_is_not_split_again()
    {
        Assert.Equal(0, await _splitter.SplitAsync(Repo, Epic("Body\n\n<!-- chargehand-split -->\n- [ ] #9"), default));

        Assert.Empty(_starter.Started);
        Assert.Empty(_github.CreatedIssues);
    }

    [Fact]
    public async Task Eleven_sub_issues_file_nothing()
    {
        Assert.Equal(0, await Split(Answer(11)));

        Assert.Empty(_github.CreatedIssues);
        Assert.Single(_github.Posted);
    }

    [Fact]
    public async Task Unparsable_answer_files_nothing()
    {
        Assert.Equal(0, await Split("I would split it into three parts."));

        Assert.Empty(_github.CreatedIssues);
        Assert.Empty(_github.EditedBodies);
        Assert.Single(_github.Posted);
    }

    [Fact]
    public async Task A_running_split_returns_without_waiting()
    {
        var epic = Epic();

        Assert.Equal(0, await _splitter.SplitAsync(Repo, epic, default));
        Assert.Equal(0, await _splitter.SplitAsync(Repo, epic, default));

        Assert.Single(_starter.Started);
        Assert.Empty(_github.CreatedIssues);
    }

    [Fact]
    public async Task A_completed_run_is_filed_on_the_next_call()
    {
        var epic = Epic();
        await _splitter.SplitAsync(Repo, epic, default);
        _log.Finish("run-1", ResultStatus.Completed, Answer(2));

        Assert.Equal(2, await _splitter.SplitAsync(Repo, epic, default));
        Assert.Equal(0, await _splitter.SplitAsync(Repo, epic, default));

        Assert.Equal(2, _github.CreatedIssues.Count);
        Assert.Single(_starter.Started);
    }

    [Fact]
    public async Task A_failure_after_two_creates_resumes_without_duplicates()
    {
        var epic = Epic();
        await _splitter.SplitAsync(Repo, epic, default);
        _log.Finish("run-1", ResultStatus.Completed, Answer(4));
        _github.CreateFailsAfter = 2;
        await Assert.ThrowsAsync<IntakeException>(() => _splitter.SplitAsync(Repo, epic, default));
        Assert.Equal(2, _github.CreatedIssues.Count);

        _github.CreateFailsAfter = null;
        Assert.Equal(4, await _splitter.SplitAsync(Repo, epic, default));

        Assert.Equal(["Part 1", "Part 2", "Part 3", "Part 4"], _github.CreatedIssues.Select(i => i.Title));
        Assert.Single(_github.EditedBodies);
        Assert.Contains("- [ ] #5000\n- [ ] #5001\n- [ ] #5002\n- [ ] #5003", _github.EditedBodies[0].Body, StringComparison.Ordinal);
        Assert.Single(_starter.Started);
    }

    [Fact]
    public async Task A_refused_epic_does_not_run_again_until_its_body_changes()
    {
        Assert.Equal(0, await Split(Answer(0)));
        var epic = Epic();
        await _splitter.SplitAsync(Repo, epic, default);
        await _splitter.SplitAsync(Repo, epic, default);
        Assert.Single(_starter.Started);

        await _splitter.SplitAsync(Repo, Epic("Do the big thing, rewritten."), default);

        Assert.Equal(2, _starter.Started.Count);
    }

    [Fact]
    public async Task Three_failed_runs_give_up_with_one_comment()
    {
        var epic = Epic();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await _splitter.SplitAsync(Repo, epic, default);
            _log.Finish($"run-{attempt}", ResultStatus.Failed, "failed");
        }
        Assert.Empty(_github.Posted);

        await _splitter.SplitAsync(Repo, epic, default);
        await _splitter.SplitAsync(Repo, epic, default);

        Assert.Equal(3, _starter.Started.Count);
        Assert.Contains("<!-- chargehand-split -->", Assert.Single(_github.Posted).Body, StringComparison.Ordinal);
        Assert.Empty(_github.CreatedIssues);
    }
}
