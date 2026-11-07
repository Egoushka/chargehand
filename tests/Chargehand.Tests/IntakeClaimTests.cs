using System.Diagnostics;
using Chargehand.Automation;

namespace Chargehand.Tests;

/// <summary>A live claim (<c>ws claim</c>, <c>refs/claims/N</c>) on an issue keeps intake from starting a batch for it; an expired claim frees it.</summary>
public class IntakeClaimTests
{
    [Fact]
    public async Task A_labelled_issue_with_a_live_claim_starts_nothing_and_says_so_once_then_starts_when_the_claim_expires()
    {
        using var rig = new IntakeRig();
        rig.Labeled(1);
        rig.Heads.Claims[1] = new IssueClaim("session-9", rig.Now.AddHours(2), rig.Now);
        await rig.Poll();
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal(("claim", 1, false), (waiting.Reason, waiting.Issue, rig.State.Handled.Contains(waiting.Key)));
        Assert.Contains("session-9", waiting.Detail, StringComparison.Ordinal);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 1 && p.Body.Contains("session-9 holds a claim", StringComparison.Ordinal));

        rig.Time.Advance(TimeSpan.FromHours(3));
        await rig.Poll();

        Assert.Equal("issue-1", Assert.Single(rig.Starter.Started).Driven!.Tasks.Single().Id);
        Assert.Empty(rig.State.Waiting);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 1 && p.Body.Contains("holds a claim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_labelled_issue_whose_claim_has_expired_starts_as_if_unclaimed()
    {
        using var rig = new IntakeRig();
        rig.Labeled(1);
        rig.Heads.Claims[1] = new IssueClaim("session-9", rig.Now.AddMinutes(-1), rig.Now.AddHours(-4));
        await rig.Poll();

        Assert.Equal("issue-1", Assert.Single(rig.Starter.Started).Driven!.Tasks.Single().Id);
        Assert.Empty(rig.State.Waiting);
        Assert.DoesNotContain(rig.GitHub.Posted, p => p.Body.Contains("holds a claim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_claim_on_another_issue_does_not_hold_this_one()
    {
        using var rig = new IntakeRig();
        rig.Labeled(2);
        rig.Heads.Claims[1] = new IssueClaim("session-9", rig.Now.AddHours(2), null);
        await rig.Poll();

        Assert.Equal("issue-2", Assert.Single(rig.Starter.Started).Driven!.Tasks.Single().Id);
    }

    [Fact]
    public async Task The_git_reader_gets_a_claim_ws_claim_pushed_and_none_once_it_is_released()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var origin = new TempDir();
        using var checkout = new TempDir();
        Git(origin.Path, "init", "-q", "-b", "main");
        Git(origin.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "--allow-empty", "-m", "one");
        Git(checkout.Path, "clone", "-q", origin.Path, ".");
        var claim = Git(origin.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit-tree", "4b825dc642cb6eb9a060e54bf8d69288fbee4904", "-m", "claim 5\n\nholder: run-77\nexpires: 2026-10-15T14:00:00Z\n");
        Git(origin.Path, "update-ref", "refs/claims/5", claim);
        List<string> warnings = [];
        var heads = new GitRepositoryHeads(warnings.Add);

        var held = await heads.ClaimAsync(checkout.Path, 5, default);

        Assert.Equal(("run-77", DateTimeOffset.Parse("2026-10-15T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), (held!.Holder, held.ExpiresAt));
        Assert.Null(await heads.ClaimAsync(checkout.Path, 6, default));

        Git(origin.Path, "update-ref", "-d", "refs/claims/5");

        Assert.Null(await heads.ClaimAsync(checkout.Path, 5, default));
        Assert.Empty(warnings);
    }

    private static string Git(string directory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }
}
