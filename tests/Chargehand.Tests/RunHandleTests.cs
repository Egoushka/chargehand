using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>Halt (and Cancel) copy the runs they hold, then cancel each; a run that finishes and disposes its token between
/// the copy and the cancel must not stop the rest (RunService.Halt).</summary>
public class RunHandleTests
{
    [Fact]
    public void CancelByCaller_after_the_run_already_disposed_its_token_does_not_throw()
    {
        var run = new RunHandle("run-1", CancellationToken.None);
        run.Dispose();

        run.CancelByCaller();

        Assert.True(run.CancelledByCaller);
    }
}
