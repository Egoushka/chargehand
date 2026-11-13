using System.Text.Json.Nodes;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Research.Api;
using Chargehand.Verification;

namespace Chargehand.Evals;

public enum DisproofOutcome { Disproved, Stands, Open, SelfDisproof }

/// <summary>A claim from a seed file: text and where it came from; the commit it is checked at is chosen by the caller.</summary>
public sealed record SeedClaim(string Text, string Source);

public sealed record BountyCounts(int Disproved, int Stands, int Open, int Skipped = 0, int Stale = 0);

/// <summary>
/// The claim bounty (spec 2026-11-11-claim-bounty): a worker of the <c>disprove</c> preset answers one claim, and only a disproof whose cited text,
/// resolved at the claim's commit, the support judge finds to contradict the claim marks it disproved and earns the preset one point.
/// </summary>
public static class ClaimBounty
{
    public const string DisprovePreset = "disprove";

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<SeedClaim> ReadSeed(IEnumerable<string> lines) =>
        [.. lines.Where(l => l.Trim().Length > 0).Select(l =>
        {
            var o = JsonNode.Parse(l)!.AsObject();
            return new SeedClaim((string)o["text"]!, (string)o["source"]!);
        })];

    /// <summary>Queues each result claim, tagged with the preset that wrote it.</summary>
    public static async Task AddResultClaimsAsync(ClaimQueue queue, ResultContract result, string runId, string preset, string commit, CancellationToken ct)
    {
        foreach (var c in result.Claims)
            await queue.AddAsync(c.Text, $"result:{runId}", commit, preset, ct);
    }

    public static async Task<DisproofOutcome> JudgeAsync(IWorkerRuntime runtime, ModelRef? judge, ClaimQueue queue, BountyLedger ledger, BountyClaim claim,
        ResultContract disproof, string runId, string preset, string repoPath, CancellationToken ct)
    {
        if (claim.Preset == preset)
            return DisproofOutcome.SelfDisproof;
        var held = (await queue.ReadAsync(ct)).FirstOrDefault(c => c.Id == claim.Id) ?? claim;
        if (held.State == ClaimState.Disproved)
            return DisproofOutcome.Disproved;
        if (disproof.Claims.Count == 0)
        {
            if (!disproof.OpenQuestions.Any(q => q.Trim().Equals("stands", StringComparison.OrdinalIgnoreCase)))
                return DisproofOutcome.Open;
            await queue.SetStateAsync(claim.Id, ClaimState.Stands, ct);
            return DisproofOutcome.Stands;
        }

        var first = disproof.Claims[0];
        var locators = disproof.Evidence.Where(e => first.Evidence.Contains(e.Id) && e.Kind == EvidenceKind.File).Select(e => e.Locator).ToList();
        var text = $"The statement \"{claim.Text}\" is false. {first.Text}";
        var scope = new EvidenceScope(repoPath, claim.Commit, new HashSet<string>(), new HashSet<string>(), "", []);
        var scored = await ClaimScorer.ScoreAsync(runtime, judge, [new BenchClaim("bounty", claim.Id, 1, text, locators)], scope, ct);
        if (scored[0].Outcome != ClaimOutcome.Supported)
            return DisproofOutcome.Open;
        await queue.TryDisproveAsync(claim.Id, () => ledger.AppendAsync(claim.Id, runId, preset, 1, ct), ct);
        return DisproofOutcome.Disproved;
    }

    /// <summary>Queues the seed claims at <paramref name="commit"/>, then hands out one open claim at a time to the disprove preset until none is left to lease. A claim that stays open is released and not tried again in the same run.</summary>
    public static async Task<BountyCounts> RunAsync(Orchestrator orchestrator, IWorkerRuntime runtime, ModelRef? judge, ClaimQueue queue, BountyLedger ledger,
        IReadOnlyList<SeedClaim> seed, string repoPath, string commit, CancellationToken ct)
    {
        HashSet<string> seeded = [];
        foreach (var s in seed)
            seeded.Add((await queue.AddAsync(s.Text, s.Source, commit, null, ct)).Id);
        var stale = await queue.MarkStaleAsync(async c => await ClaimQueue.IsAncestorAsync(repoPath, c, commit, ct) ?? true, ct);
        var skipped = (await queue.ReadAsync(ct)).Count(c => seeded.Contains(c.Id) && c.State != ClaimState.Open);
        int disproved = 0, stands = 0, open = 0;
        HashSet<string> tried = [];
        while (await queue.LeaseNextAsync(Lease, ct, tried) is { } claim)
        {
            tried.Add(claim.Id);
            var runId = RunIds.New();
            var request = new RunRequest("request/v1", "Show that the claim is false, or that it stands.", new RequestContext(false, DisprovePreset, Repository: new RepositoryRef(repoPath, claim.Commit)),
                [new CallerInput("claim", "claim", claim.Text)], []);
            var result = await orchestrator.RunAsync(request, ct, runId);
            switch (result.Status == ResultStatus.Completed ? await JudgeAsync(runtime, judge, queue, ledger, claim, result, runId, DisprovePreset, repoPath, ct) : DisproofOutcome.Open)
            {
                case DisproofOutcome.Disproved:
                    disproved++;
                    break;
                case DisproofOutcome.Stands:
                    stands++;
                    break;
                default:
                    open++;
                    await queue.ReleaseAsync(claim.Id, ct);
                    break;
            }
        }
        return new BountyCounts(disproved, stands, open, skipped, stale);
    }
}
