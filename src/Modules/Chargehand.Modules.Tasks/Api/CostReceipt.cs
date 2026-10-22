using System.Globalization;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>The line at the foot of a task's pull request body (CHARGEHAND-288): what the task cost in the governor's dollar equivalent (its tokens at the profile's prices, also on a subscription),
/// its turns and the continuation sessions it needed. The figure is the task's at handover; a CI fix session's spend comes after and is in the learning record, not here.</summary>
public static class CostReceipt
{
    /// <param name="total">The whole task's usage, every session's.</param>
    /// <param name="continuations">Sessions started after the first because one was stopped (ADR 0044).</param>
    public static string Footer(SessionOutcome total, int continuations)
    {
        var cost = total.UsdEstimate is { } usd
            ? string.Create(CultureInfo.InvariantCulture, $"{(total.UsdEstimatePartial ? "at least " : "")}${usd:0.00} notional{(total.UsdEstimatePartial ? " (a model had no price)" : "")}")
            : "not priced";
        var turns = string.Create(CultureInfo.InvariantCulture, $"{total.Turns} {(total.Turns == 1 ? "turn" : "turns")}");
        var more = continuations == 0 ? "no continuations" : string.Create(CultureInfo.InvariantCulture, $"{continuations} {(continuations == 1 ? "continuation" : "continuations")}");
        return $"\n\n---\nchargehand cost receipt: {cost}, {turns}, {more}.";
    }
}
