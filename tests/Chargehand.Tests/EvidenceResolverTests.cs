using System.Diagnostics;
using Chargehand.Contracts;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Tests;

public sealed class EvidenceResolverTests : IDisposable
{
    private readonly TempDir _repo = new();
    private readonly string _head;

    public EvidenceResolverTests()
    {
        _repo.Write("src/calc.py", "def add(a, b):\n    return a + b\n\n\ndef divide(a, b):\n    return a / b\n");
        Git("init", "-q");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-qm", "init");
        _head = Git("rev-parse", "HEAD").Trim();
        _repo.Write("src/calc.py", "changed after the commit\n");
    }

    public void Dispose() => _repo.Dispose();

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo.Path, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    private async Task<IReadOnlyList<EvidenceFailure>> Resolve(params Evidence[] evidence)
    {
        var contract = new ResultContract("result/v1", "t", "n", new string('0', 32),
            new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-26")), ResultStatus.Completed, "s",
            [new Claim("c", evidence.Select(e => e.Id).ToList(), 1)], evidence, [], [], 1, new Usage(0, 0, 0, 0, 0));
        var scope = new EvidenceScope(_repo.Path, _head, ["msg_1"], ["fact-1"], "see https://example.com/a for details",
            [new FileDiff("src/calc.py", "@@ -1,2 +1,3 @@\n", 1, 0, "modified")]);
        return await new GitEvidenceResolver().ResolveAsync(contract, scope, CancellationToken.None);
    }

    [Fact]
    public async Task File_lines_are_checked_at_the_commit_not_the_working_tree()
    {
        Assert.Empty(await Resolve(new Evidence("e1", EvidenceKind.File, "src/calc.py:5-6")));
        var f = Assert.Single(await Resolve(new Evidence("e1", EvidenceKind.File, "src/calc.py:7")));
        Assert.Contains("6 lines", f.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/missing.py:1")]
    [InlineData("src/calc.py")]
    [InlineData("src/calc.py:0")]
    [InlineData("src/calc.py:4-2")]
    [InlineData("../outside.txt:1")]
    public async Task Bad_file_locators_fail(string locator) =>
        Assert.Single(await Resolve(new Evidence("e1", EvidenceKind.File, locator)));

    [Fact]
    public async Task Evidence_commit_must_match_the_node_commit()
    {
        Assert.Empty(await Resolve(new Evidence("e1", EvidenceKind.File, "src/calc.py:1", Commit: _head[..7])));
        Assert.Single(await Resolve(new Evidence("e1", EvidenceKind.File, "src/calc.py:1", Commit: "1234567")));
    }

    [Fact]
    public async Task Commit_session_message_input_and_url_kinds()
    {
        Assert.Empty(await Resolve(
            new Evidence("c", EvidenceKind.Commit, _head),
            new Evidence("m", EvidenceKind.SessionMessage, "msg_1"),
            new Evidence("i", EvidenceKind.Input, "fact-1"),
            new Evidence("u", EvidenceKind.Url, "https://example.com/a"),
            new Evidence("d", EvidenceKind.Diff, "src/calc.py")));
        var failures = await Resolve(
            new Evidence("c", EvidenceKind.Commit, "deadbeef"),
            new Evidence("m", EvidenceKind.SessionMessage, "msg_2"),
            new Evidence("i", EvidenceKind.Input, "fact-9"),
            new Evidence("u", EvidenceKind.Url, "https://example.com/never-seen"),
            new Evidence("d", EvidenceKind.Diff, "src/other.py"));
        Assert.Equal(["c", "m", "i", "u", "d"], failures.Select(f => f.EvidenceId));
    }
}
