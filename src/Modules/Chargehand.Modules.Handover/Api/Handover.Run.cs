using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Handover.Api;

public sealed partial class Handover
{
    /// <summary>What one handover has found so far, so that every phase of <see cref="RunAsync"/> refuses with the same fields.</summary>
    private sealed class HandoverRun(HandoverInput input, List<string> secrets)
    {
        public HandoverInput Input { get; } = input;

        /// <summary>The request's secrets and the push credential: a diff must not carry them, and no reason or detail shows them.</summary>
        public List<string> Secrets { get; } = secrets;

        public string Bundle { get; } = Path.Combine(input.OutDirectory, "chargehand.bundle");

        public string Scratch { get; } = Path.Combine(input.ScratchRoot, input.RunId);

        public Dictionary<string, string> Env { get; } = GitProcess.Environment(input.ScratchRoot);

        public string Commit { get; set; } = "";
        public List<string> Changed { get; set; } = [];

        public ChangeSummary? Changes { get; set; }

        public VerificationRecord? Verification { get; set; }

        public MutationOutcome? Mutation { get; set; }

        public IReadOnlyList<string> VerificationPaths { get; set; } = [];

        /// <summary>Why a branch with no test command was pushed all the same (<see cref="NoTestsRuleAsync"/>); null otherwise.</summary>
        public string? NoTestsRule { get; set; }

        /// <summary>With <see cref="HandoverInput.CiVerifies"/>: what the pull request says verifies it (<see cref="CiVerdictAsync"/>); null otherwise.</summary>
        public string? CiVerdict { get; set; }

        public HandoverOutcome Refuse(string reason, ErrorCode error, VerificationRecord? verification = null, IReadOnlyList<string>? changed = null, IReadOnlyList<ScanFinding>? findings = null,
            IReadOnlyList<string>? verificationPaths = null, string? commit = null) =>
            new(HandoverStatus.NotPushed, Redact(reason, Secrets), error, Input.Branch, commit, null, verification, changed ?? [], verificationPaths ?? [], findings ?? [], Changes, Mutation: Mutation);
    }
}
