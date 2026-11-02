namespace Chargehand.Runtimes.ClaudeCode.Api;

/// <summary><c>chargehand budget</c>: shows and sets the owner's two ignores (<see cref="BudgetIgnore"/>) in the state directory beside the run log. Exit 0 done, 1 nothing to clear, 2 bad use.</summary>
public static class BudgetCli
{
    public static int Run(IReadOnlyList<string> args, string directory, string user, TimeProvider time, TextWriter output, TextWriter error)
    {
        var ignore = new BudgetIgnore(directory, time);
        switch (args)
        {
            case [] or ["status"]:
                foreach (var kind in new[] { BudgetIgnore.Subscription, BudgetIgnore.Api })
                    output.WriteLine(ignore.Active(kind) is { } r ? $"{kind}: ignored until {r.Until:u} ({r.Reason}, set by {r.By})" : $"{kind}: enforced");
                return 0;
            case ["ignore", var kind, "--reason", var reason] when BudgetIgnore.IsKind(kind) && !string.IsNullOrWhiteSpace(reason):
                var set = ignore.Set(kind, reason, user);
                output.WriteLine($"{kind}: ignored until {set.Until:u}. The other budget is untouched.");
                return 0;
            case ["clear", var kind] when BudgetIgnore.IsKind(kind):
                output.WriteLine(ignore.Clear(kind, user) ? $"{kind}: enforced again" : $"{kind}: no ignore in force");
                return 0;
            default:
                error.WriteLine("usage: chargehand budget [status] | ignore <subscription|api> --reason <why> | clear <subscription|api>  (a reason is required; an ignore lasts four hours)");
                return 2;
        }
    }
}
