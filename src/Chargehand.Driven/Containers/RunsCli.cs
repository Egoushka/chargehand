namespace Chargehand.Containers;

/// <summary><c>chargehand runs kill --all</c>: removes every driven-session container, and the networks made for them, by their label (ADR 0039).
/// It needs neither a running server nor the run log, so it works when the server is the thing that has gone wrong. On a host where the
/// container engine belongs to a runner service, run it there, or call the runner's <c>POST /kill-all</c>.</summary>
public static class RunsCli
{
    public const string Usage = "usage: chargehand runs kill --all";

    public static async Task<int> KillAllAsync(IContainerEngine engine, TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            await engine.KillAllAsync(ct);
            output.WriteLine("removed every session container and batch network that carries the chargehand label");
            return 0;
        }
        catch (ChargehandException e)
        {
            error.WriteLine($"{e.Message} {e.Action}".Trim());
            return 1;
        }
    }
}
