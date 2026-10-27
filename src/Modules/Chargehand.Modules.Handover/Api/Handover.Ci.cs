using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Handover.Api;

public sealed partial class Handover
{
    /// <summary>ADR 0078: the repository's CI verifies, so no fresh-container run happens here. The sentence for the pull request says which checks decide the merge and what the
    /// session driver ran before the push; a repository with no CI and no declared check is refused unless the change is documentation only.</summary>
    private async Task<HandoverOutcome?> CiVerdictAsync(HandoverRun run, CancellationToken ct)
    {
        var input = run.Input;
        var repository = RemoteRepository.Parse(input.RemoteUrl, forge) ?? (input.AllowLocalRemote ? new RemoteRepository("local", "local") : null);
        var gate = pullRequests is IBranchRules rules && repository is not null ? await rules.ChecksGateAsync(repository, input.BaseBranch, ct) : ChecksGate.Unknown;
        var ci = RepositoryReadiness.CiPresent(run.Scratch);
        var check = input.PrePushCheck is { Count: > 0 } declared ? declared : null;
        if (gate != ChecksGate.Required && !ci && check is null && !input.AllowNoTests && !input.Partial && !run.Changed.All(IsDocumentation))
            return run.Refuse("the repository has no CI and no check (context.verify, driven.services.verify or scripts/check.sh); chargehand does not push a change nothing checks",
                ErrorCode.VerificationFailed, changed: run.Changed, commit: run.Commit);
        run.CiVerdict = CiVerdict(gate, ci, input.BaseBranch, check, input.Partial);
        return null;
    }

    private static string CiVerdict(ChecksGate gate, bool ci, string baseBranch, IReadOnlyList<string>? check, bool partial)
    {
        var decides = gate == ChecksGate.Required ? $"The base branch {baseBranch} requires status checks; they decide the merge."
            : ci ? "No required check; a person merges."
            : "The repository has no CI; a person merges.";
        var filter = check is null ? "Before the push, no check ran: the repository declares none (context.verify, driven.services.verify or scripts/check.sh)."
            : partial ? $"Before the push, the session driver ran `{string.Join(' ', check)}` in the session's container; the session did not finish, so it may not have passed. That is a filter, not proof."
            : $"Before the push, the session driver ran `{string.Join(' ', check)}` in the session's container and it passed. That is a filter, not proof.";
        return $"{decides} {filter}";
    }
}
