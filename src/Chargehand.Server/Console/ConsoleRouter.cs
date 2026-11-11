using Chargehand.Modules.Economics.Api;

namespace Chargehand.Server;

/// <summary><c>router/v1</c>: the budget router's pools and routes (ADR 0095), read from the profile's <c>pools</c> block and the spend the run log holds. Owner only; off when the profile has no block.</summary>
internal static class ConsoleRouter
{
    public static async Task<RouterReport> BuildAsync(ConsoleContext c, CancellationToken ct)
    {
        if (c.Profile?.Pools is not { Enabled: true } pools)
            return RouterReportBuilder.Build(null, [], c.Now);
        return RouterReportBuilder.Build(pools, PoolRouting.SpendOf((await c.IndexAsync(ct)).Data.Runs), c.Now);
    }
}
