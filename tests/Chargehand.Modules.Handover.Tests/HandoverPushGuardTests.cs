using System.Diagnostics;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>Issue 430: a branch that carries scratch output, too many files or a download-sized file is not pushed, and a <c>WIP:</c> commit is renamed before the push.</summary>
public class HandoverPushGuardTests
{
    private const string Stopped = "WIP: session stopped by turn_limit after 80 turns";

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {output}");
        return output.Trim();
    }

    private static void Write(string dir, string path, string text = "x\n")
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static Task<HandoverOutcome> Hand(HandoverTests.Fixture f, Func<HandoverInput, HandoverInput>? with = null) =>
        new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(f.Input(with: with), default);

    [Theory]
    [InlineData(".tmp/NuGetScratch/lock.json", "tmpdir")]
    [InlineData("src/.tmp/x.json", "tmpdir")]
    [InlineData("packages/restore-1a2b.tmp", "tmp-file")]
    [InlineData(".scratch-probe.txt", "scratch")]
    [InlineData(".scratch/probe.sh", "scratch")]
    public async Task A_branch_with_scratch_output_is_not_pushed(string path, string kind)
    {
        using var f = new HandoverTests.Fixture(edit: dir => Write(dir, path));

        var outcome = await Hand(f);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.SessionFailed, outcome.Error);
        Assert.Contains($"{kind}: {path}", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task Scratch_output_a_later_commit_removed_is_still_refused_because_the_history_is_pushed()
    {
        using var f = new HandoverTests.Fixture(edit: dir => Write(dir, ".tmp/nuget.lock"), thenEdit: dir => File.Delete(Path.Combine(dir, ".tmp/nuget.lock")));

        var outcome = await Hand(f);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("tmpdir: .tmp/nuget.lock", outcome.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/template.txt")]
    [InlineData("docs/tmp.md")]
    [InlineData("tmp/notes.txt")]
    [InlineData("src/Temp.cs")]
    [InlineData("src/scratchpad.cs")]
    public void An_ordinary_path_is_not_junk(string path) => Assert.Null(PushGuard.Junk(path));

    [Fact]
    public async Task A_branch_that_touches_more_files_than_the_limit_is_not_pushed()
    {
        using var f = new HandoverTests.Fixture(edit: dir =>
        {
            for (var i = 0; i < PushGuard.MaxBranchFiles; i++)
                Write(dir, $"gen/f{i}.txt");
        });

        var outcome = await Hand(f);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains($"{PushGuard.MaxBranchFiles + 1} files", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task A_branch_at_the_file_limit_is_pushed()
    {
        using var f = new HandoverTests.Fixture(edit: dir =>
        {
            for (var i = 0; i < PushGuard.MaxBranchFiles - 1; i++)
                Write(dir, $"gen/f{i}.txt");
        });

        Assert.Equal(HandoverStatus.Pushed, (await Hand(f)).Status);
    }

    [Fact]
    public async Task A_branch_with_a_file_over_the_size_limit_is_not_pushed_even_when_a_later_commit_removed_it()
    {
        using var f = new HandoverTests.Fixture(edit: dir => Write(dir, "dist/app.bin", new string('a', (int)PushGuard.MaxFileBytes + 1)),
            thenEdit: dir => File.Delete(Path.Combine(dir, "dist/app.bin")));

        var outcome = await Hand(f);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("dist/app.bin", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("MiB", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task A_file_at_the_size_limit_is_pushed()
    {
        using var f = new HandoverTests.Fixture(edit: dir => Write(dir, "data/fixture.bin", new string('a', (int)PushGuard.MaxFileBytes)));

        Assert.Equal(HandoverStatus.Pushed, (await Hand(f)).Status);
    }

    [Fact]
    public async Task The_stop_snapshots_wip_commit_is_renamed_before_the_push_and_keeps_its_tree_and_body()
    {
        using var f = new HandoverTests.Fixture(thenEdit: dir => Write(dir, "more.txt"), thenMessage: [Stopped, "not committed (over 5 MiB): big.bin"]);
        var before = Git(f.Work, "log", "--format=%T %an %aI", $"{f.BaseCommit}..chargehand/run1");

        var outcome = await Hand(f);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal(outcome.Commit, f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Equal(["chore: unfinished work, session stopped by turn_limit after 80 turns", "feat: retry"],
            Git(f.Remote, "log", "--format=%s", $"{f.BaseCommit}..chargehand/run1").Split('\n'));
        Assert.Equal("not committed (over 5 MiB): big.bin", Git(f.Remote, "log", "-1", "--format=%b", "chargehand/run1"));
        Assert.Equal(before, Git(f.Remote, "log", "--format=%T %an %aI", $"{f.BaseCommit}..chargehand/run1"));
    }

    [Fact]
    public async Task A_wip_commit_is_renamed_with_the_trailers_and_a_repeated_handover_makes_the_same_commit()
    {
        using var f = new HandoverTests.Fixture(thenEdit: dir => Write(dir, "more.txt"), thenMessage: [Stopped]);
        var input = (Func<HandoverInput, HandoverInput>)(i => i with { Provenance = new Provenance("run1", "t-1", "claude-code 2.1.0", "sonnet", 0.5m, 1200, 40, "checks passed") });

        var first = await Hand(f, input);
        var second = await Hand(f, input);

        Assert.Equal(HandoverStatus.Pushed, second.Status);
        Assert.Equal(first.Commit, second.Commit);
        var head = Git(f.Remote, "log", "-1", "--format=%B", "chargehand/run1");
        Assert.StartsWith("chore: unfinished work, session stopped by turn_limit", head, StringComparison.Ordinal);
        Assert.Equal("run1", Provenance.Read(head)[Provenance.RunKey]);
    }

    [Theory]
    [InlineData("WIP: session stopped by no_progress after 4 turns", "chore: unfinished work, session stopped by no_progress after 4 turns")]
    [InlineData("wip add the retry", "chore: unfinished work, add the retry")]
    [InlineData("[WIP] add the retry\n\nbody", "chore: unfinished work, add the retry\n\nbody")]
    [InlineData("WIP", "chore: unfinished work, the session's work so far")]
    [InlineData("feat: wipe the cache", "feat: wipe the cache")]
    [InlineData("wipe the cache", "wipe the cache")]
    [InlineData("fix: the WIP: prefix", "fix: the WIP: prefix")]
    public void A_wip_subject_is_renamed_and_any_other_is_kept(string message, string renamed) => Assert.Equal(renamed, PushGuard.RenameWip(message));
}
