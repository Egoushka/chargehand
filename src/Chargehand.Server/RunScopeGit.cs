using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Server;

/// <summary>The git half of <see cref="RunScope"/>: how a call's commit relates to the task's, asked of the task's repository.</summary>
public static class RunScopeGit
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    /// <summary><see cref="RunScope.Apply"/> with the call's commit related to the task's through git in the task's repository.</summary>
    public static async Task<(RunRequest? Request, string? Refusal)> ApplyAsync(RunTokenClaims claims, RunRequest request, RunTokenLedger ledger, CancellationToken ct) =>
        RunScope.Apply(claims, request, ledger, request.Context.Repository is { } repository ? await RelateAsync(claims, repository.Commit, ct) : CommitRelation.Unrelated);

    /// <summary>How <paramref name="commit"/> relates to the token's commit in the token's repository; a failed git call counts as unrelated.</summary>
    public static async Task<CommitRelation> RelateAsync(RunTokenClaims claims, string commit, CancellationToken ct)
    {
        if (string.Equals(commit, claims.Commit, StringComparison.OrdinalIgnoreCase))
            return CommitRelation.Descendant;
        if (!Directory.Exists(claims.RepositoryPath) || commit.Length is < 4 or > 64 || !commit.All(char.IsAsciiHexDigit))
            return CommitRelation.Unrelated;
        try
        {
            var env = GitProcess.Environment(Path.GetTempPath());
            var known = await GitProcess.RunAsync(claims.RepositoryPath, env, ["cat-file", "-e", $"{commit}^{{commit}}"], GitTimeout, ct);
            if (known.ExitCode != 0)
                return CommitRelation.Unknown;
            var ancestor = await GitProcess.RunAsync(claims.RepositoryPath, env, ["merge-base", "--is-ancestor", claims.Commit, commit], GitTimeout, ct);
            return ancestor.ExitCode == 0 ? CommitRelation.Descendant : CommitRelation.Unrelated;
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommitRelation.Unrelated;
        }
    }
}
