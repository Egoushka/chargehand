using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Oversight.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary><c>intake-rank/v1</c>: the candidates sent, highest expected value first.</summary>
public sealed record IntakeRankBody(string ContractVersion, IReadOnlyList<RankedCandidate> Candidates);

/// <summary><c>POST /v1/intake/rank</c> (issue #439): ranks the work items a caller sends, GitHub issues or tracker items alike, by completion probability × priority ÷ predicted cost from the
/// pre-flight forecast (<see cref="CandidateRanking"/>). The owner's alone, like the other intake routes; it reads the learning records and changes nothing.</summary>
internal static class ConsoleRank
{
    public const string Contract = "intake-rank/v1";
    public const int MaxCandidates = 200;

    private sealed record RankRequest(IReadOnlyList<RankRequestCandidate?>? Candidates);

    private sealed record RankRequestCandidate(string? Id, string? Repository, string? Goal, string? Size, double? Priority);

    public static void Map(WebApplication app, ServerSettings settings, Chargehand.Driven.DrivenRun? driven)
    {
        app.MapPost("/v1/intake/rank", async (HttpContext ctx) =>
        {
            if (ChargehandServer.OwnerOnly(ctx) is { } refused)
                return refused;
            RankRequest? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<RankRequest>(ContractJson.Options, ctx.RequestAborted);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                body = null;
            }
            if (Parse(body) is not { } candidates)
                return ChargehandServer.Json(ChargehandServer.Problem($"candidates must list 1 to {MaxCandidates} items, each with an id and a repository, a size of S, M or L if any, and a priority of 1 or more if any"),
                    StatusCodes.Status400BadRequest);
            IReadOnlyList<LearningRecord> records = driven?.Learning is { } store ? [.. (await store.ReadAsync(ctx.RequestAborted)).Select(e => e.Record)] : [];
            var ranked = CandidateRanking.Rank(settings.Profile?.Driven?.Governor ?? new DrivenGovernor(), candidates, records, (settings.Clock ?? TimeProvider.System).GetUtcNow());
            return ChargehandServer.Json(new IntakeRankBody(Contract, ranked), StatusCodes.Status200OK);
        });
    }

    /// <returns>Null when the list is missing, empty, too long, or holds an item that is not valid.</returns>
    private static List<RankCandidate>? Parse(RankRequest? body)
    {
        if (body?.Candidates is not { Count: > 0 and <= MaxCandidates } items)
            return null;
        List<RankCandidate> candidates = [];
        foreach (var item in items)
        {
            if (item is not { Id: { Length: > 0 } id, Repository: { Length: > 0 } repository } || item.Size is not (null or "S" or "M" or "L")
                || item.Priority is { } p && (!double.IsFinite(p) || p < 1))
                return null;
            candidates.Add(new RankCandidate(id, repository, item.Goal, item.Size, item.Priority ?? 1));
        }
        return candidates;
    }
}
