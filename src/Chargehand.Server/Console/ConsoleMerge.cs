using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary>A person merges a pull request chargehand opened, from the console (cockpit increment 4). The owner only: a merge changes the repository for everyone. The integrator's policy and the forge's
/// required checks decide (<see cref="ManualMerge"/>); a refusal is a <c>409</c> with the reason, and the merge is pinned to the head commit that was read, so a push in between fails at the forge.
/// A second request finds the pull request merged and changes nothing. Writes an <c>operator</c> event to the batch.</summary>
internal static class ConsoleMerge
{
    public static void Map(WebApplication app, Func<HttpContext, ConsoleContext> context) =>
        app.MapPost("/v1/pulls/{owner}/{repo}/{number:int}/merge", async (string owner, string repo, int number, HttpContext ctx) =>
            ChargehandServer.OwnerOnly(ctx) ?? await MergeAsync(context(ctx), PrincipalHttp.Of(ctx), owner, repo, number, ctx.RequestAborted));

    private static async Task<IResult> MergeAsync(ConsoleContext c, Principal? who, string owner, string repo, int number, CancellationToken ct)
    {
        var entry = (await c.LearningAsync(ct)).Where(e => e.Record.Pr is { } pr && pr.Number == number && pr.Remote.Owner == owner && pr.Remote.Name == repo)
            .OrderBy(e => e.Record.At).LastOrDefault();
        if (entry?.Record.Pr is not { } pull)
            return Microsoft.AspNetCore.Http.Results.NotFound();
        if (c.Driven?.ReadHost(pull.Remote) is not { } host)
            return Refused("this server has no credential for that pull request's forge");
        try
        {
            var snapshot = await host.GetAsync(pull.Remote, number, ct);
            if (snapshot.Merged)
                return ChargehandServer.Json(new { pull = pull.Url, number, action = "merge", changed = false }, StatusCodes.Status200OK);
            var gate = await host.ChecksGateAsync(pull.Remote, snapshot.BaseBranch, ct);
            if (ManualMerge.Refusal(c.IntegrationOf(pull.Remote), gate, snapshot) is { } why)
                return Refused(why);
            await host.MergeNowAsync(pull.Remote, number, snapshot.HeadSha, ct);
            ConsoleActions.Tell(c, who, entry.Record.BatchId, entry.Record.TaskId, $"pull request #{number} merged by an operator");
            return ChargehandServer.Json(new { pull = pull.Url, number, action = "merge", changed = true }, StatusCodes.Status202Accepted);
        }
        catch (IntegrationException e)
        {
            return ChargehandServer.Json(ChargehandServer.Problem($"the forge refused: {e.Message}"), StatusCodes.Status502BadGateway);
        }
    }

    private static IResult Refused(string why) => ChargehandServer.Json(ChargehandServer.Problem(why), StatusCodes.Status409Conflict);
}
