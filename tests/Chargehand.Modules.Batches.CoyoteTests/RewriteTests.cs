using System.Reflection;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.CoyoteTests;

/// <summary>A build can copy the original assemblies back over the rewritten ones; the scenarios then run on the thread pool and report bugs that are not in the code. This names that state instead.</summary>
public class RewriteTests
{
    [Theory]
    [InlineData(typeof(BatchScheduler))]
    [InlineData(typeof(TaskRunner))]
    [InlineData(typeof(RewriteTests))]
    public void The_assemblies_the_scenarios_run_are_rewritten_by_Coyote(Type type) =>
        Assert.Contains(type.Assembly.GetReferencedAssemblies(), a => a.Name == "Microsoft.Coyote");
}
