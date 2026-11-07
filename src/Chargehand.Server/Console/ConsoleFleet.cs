using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Server;

/// <summary>A run or batch this process holds. <c>blocked_by</c> is always present on all three item kinds, null when nothing blocks the item. <paramref name="BlockedBy"/> is <c>waiting_for_slot</c> (queued behind the run gate) or <c>rate_limit</c> (a batch held by a provider limit); null: nothing.</summary>
public sealed record FleetRunBody(string Id, string Kind, string? Actor, string State, DateTimeOffset Since, long AgeSeconds, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BlockedBy);

/// <summary>A <c>refs/claims/&lt;n&gt;</c> ref in a checkout. <paramref name="State"/> is <c>held</c> or <c>expired</c> (free to take); an expired claim blocks nothing, so <paramref name="BlockedBy"/> is always null.</summary>
public sealed record FleetClaimBody(string Repository, int Issue, string Holder, string State, DateTimeOffset ExpiresAt, DateTimeOffset? Since, long? AgeSeconds, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BlockedBy);

/// <summary>A pull request chargehand opened and still reads as open. <paramref name="State"/> is <c>open</c> or <c>awaiting_person</c>; <paramref name="BlockedBy"/> is then why:
/// <c>policy_ask</c>, <c>test_integrity</c> or <c>partial_draft</c>.</summary>
public sealed record FleetPullBody(string Url, int? Number, string? Repository, string? Actor, string State, DateTimeOffset? Since, long? AgeSeconds, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BlockedBy);

/// <summary><c>fleet/v1</c>: what is running, claimed and open, with each item's state, age and blocker, in one read. It feeds the action log (ADR 0090) and the approvals app's "Needs you" view.</summary>
public sealed record FleetBody(string ContractVersion, DateTimeOffset GeneratedAt, IReadOnlyList<FleetRunBody> Runs, IReadOnlyList<FleetClaimBody> Claims, IReadOnlyList<FleetPullBody> PullRequests,
    IReadOnlyList<string> NotRecorded);

internal static class ConsoleFleet
{
    public const string Contract = "fleet/v1";

    private const int MaxPullRequests = 100;
    private const string ClaimPrefix = "refs/claims/";

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "Runs are the unfinished top-level runs and batches this process holds; a restart drops a queued run's age, a resumed batch counts from its resume.",
        "Claims are the refs/claims/<n> refs already in a checkout under repository_roots (this route fetches nothing); a ref whose holder or expiry cannot be read is left out.",
        "Pull requests are the ones chargehand opened and has a record of, with the state last read back from the forge; a pull request's CI is not read here.",
    ];

    public static async Task<FleetBody> BuildAsync(ConsoleContext c, Principal? who, CancellationToken ct)
    {
        var now = c.Now;
        var pauses = c.Driven?.RateLimitPauses(now).Select(p => p.BatchRunId).ToHashSet(StringComparer.Ordinal) ?? [];
        var runs = c.Runs.Held.Where(r => !r.IsChild && Principal.Sees(who, r.Actor)).Select(r => RunOf(r, pauses, now)).OrderBy(r => r.Since).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var pulls = (await c.LearningAsync(ct)).Where(e => Principal.Sees(who, e.Record.Actor) && e.Record.Pr is not null && (e.PrOutcome?.Outcome ?? PrOutcomes.Open) == PrOutcomes.Open)
            .OrderByDescending(e => e.Record.At).Take(MaxPullRequests).Select(e => PullOf(ConsoleBatches.PullRequestOf(c, e)!, now)).ToList();
        return new FleetBody(Contract, now, runs, await ClaimsAsync(c, who, now, ct), pulls, NotRecorded);
    }

    private static FleetRunBody RunOf(RunHandle run, HashSet<string> pauses, DateTimeOffset now)
    {
        var since = run.History.Select(e => e.At).FirstOrDefault(at => at is not null) ?? now;
        var state = run.Latest.Status;
        var blocked = pauses.Contains(run.Id) ? "rate_limit" : state == RunState.Queued ? "waiting_for_slot" : null;
        return new FleetRunBody(run.Id, run.IsBatch ? "batch" : "run", run.Actor, JsonNamingPolicy.SnakeCaseLower.ConvertName(state.ToString()), since, Age(since, now), blocked);
    }

    private static FleetPullBody PullOf(PullRequestBody pr, DateTimeOffset now) =>
        new(pr.Url, pr.Number, pr.Repository, pr.Actor, pr.AwaitingPerson ? "awaiting_person" : "open", pr.OpenedAt, pr.OpenedAt is { } at ? Age(at, now) : null, pr.AwaitingReason);

    private static long Age(DateTimeOffset since, DateTimeOffset now) => Math.Max(0, (long)(now - since).TotalSeconds);

    private static async Task<IReadOnlyList<FleetClaimBody>> ClaimsAsync(ConsoleContext c, Principal? who, DateTimeOffset now, CancellationToken ct)
    {
        List<FleetClaimBody> claims = [];
        foreach (var root in (c.Profile?.Roots ?? []).Where(Directory.Exists))
            foreach (var path in ConsoleRepositories.Candidates(root))
                if (who?.Account is not { Repositories: { Count: > 0 } allowed } || allowed.Any(a => ConsoleRepositories.Within(a, path)))
                    claims.AddRange(await ClaimsOfAsync(path, now, ct));
        return [.. claims.OrderBy(k => k.Repository, StringComparer.Ordinal).ThenBy(k => k.Issue)];
    }

    private static async Task<IEnumerable<FleetClaimBody>> ClaimsOfAsync(string path, DateTimeOffset now, CancellationToken ct)
    {
        var listed = await ConsoleRepositories.GitAsync(path, ["for-each-ref", "--format=%(refname)%09%(objecttype)%09%(creatordate:iso-strict)", ClaimPrefix], ct);
        if (listed.ExitCode != 0 || listed.Output.Length == 0)
            return [];
        List<FleetClaimBody> found = [];
        foreach (var line in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2 || !int.TryParse(parts[0].AsSpan(ClaimPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var issue))
                continue;
            var content = await ConsoleRepositories.GitAsync(path, ["cat-file", "-p", parts[0]], ct);
            DateTimeOffset? made = parts.Length > 2 && DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            if (content.ExitCode == 0 && Claim.Parse(content.Output, parts[1]) is { } claim)
                found.Add(new FleetClaimBody(Path.GetFileName(path), issue, claim.Holder, claim.ExpiresAt > now ? "held" : "expired", claim.ExpiresAt, claim.At ?? made, (claim.At ?? made) is { } since ? Age(since, now) : null, null));
        }
        return found;
    }

    /// <summary>A claim's holder and expiry as the ref's object records them: a JSON object, or <c>key: value</c> lines, in a blob or in a commit's or tag's message. Null when either is missing or unreadable.</summary>
    internal sealed record Claim(string Holder, DateTimeOffset ExpiresAt, DateTimeOffset? At)
    {
        public static Claim? Parse(string text, string objectType)
        {
            if (objectType is "commit" or "tag" && text.IndexOf("\n\n", StringComparison.Ordinal) is var cut and >= 0)
                text = text[(cut + 2)..];
            var fields = Fields(text.Trim());
            var holder = First(fields, "holder", "session", "run");
            var expires = Time(First(fields, "expires_at", "expires", "expiry"));
            return string.IsNullOrWhiteSpace(holder) || expires is not { } until ? null : new Claim(holder, until, Time(First(fields, "claimed_at", "at", "since")));
        }

        private static Dictionary<string, string> Fields(string text)
        {
            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
            if (text.StartsWith('{'))
            {
                try
                {
                    using var json = JsonDocument.Parse(text);
                    if (json.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number))
                            fields[p.Name] = p.Value.ToString();
                }
                catch (JsonException)
                {
                    // Unreadable: no fields, so no claim.
                }
                return fields;
            }
            foreach (var line in text.Split('\n'))
                if (line.IndexOfAny([':', '=']) is var i and > 0)
                    fields[line[..i].Trim()] = line[(i + 1)..].Trim();
            return fields;
        }

        private static string? First(Dictionary<string, string> fields, params string[] names) => names.Select(n => fields.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        private static DateTimeOffset? Time(string? text) =>
            text is null ? null : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    }
}
