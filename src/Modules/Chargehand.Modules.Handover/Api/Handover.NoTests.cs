using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Handover.Api;

public sealed partial class Handover
{
    /// <summary>A branch of a repository with no test command is pushed only when nothing is lost by not testing it (CHARGEHAND-245): every changed file is documentation, or the base branch's
    /// rules require status checks, so the forge's own CI stands between the pull request and a merge. The sentence the pull request body gives; null: refused, as before.</summary>
    private async Task<string?> NoTestsRuleAsync(HandoverRun run, CancellationToken ct)
    {
        var input = run.Input;
        if (run.Changed.All(IsDocumentation))
            return "every changed file is documentation (*.md, docs/)";
        var repository = RemoteRepository.Parse(input.RemoteUrl, forge) ?? (input.AllowLocalRemote ? new RemoteRepository("local", "local") : null);
        if (pullRequests is IBranchRules rules && repository is not null && await rules.ChecksGateAsync(repository, input.BaseBranch, ct) == ChecksGate.Required)
            return $"the base branch {input.BaseBranch} requires status checks, so the repository's own CI runs before any merge";
        return null;
    }

    public static bool IsDocumentation(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.StartsWith("docs/", StringComparison.Ordinal);
}
