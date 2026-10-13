using System.Reflection;

namespace Chargehand.Tests;

/// <summary>ADR 0047: the orchestrator core ships without the driven infrastructure. A host with Core, Contracts and the adapters can answer read-only runs.</summary>
public class ArchitectureTests
{
    private const string Driven = "Chargehand.Driven";

    private static IEnumerable<string> References(string assembly) =>
        Assembly.Load(assembly).GetReferencedAssemblies().Select(a => a.Name!);

    [Theory]
    [InlineData("Chargehand")]
    [InlineData("Chargehand.Contracts")]
    [InlineData("Chargehand.Mcp")]
    [InlineData("Chargehand.OpenCode")]
    [InlineData("Chargehand.ClaudeCode")]
    public void Core_and_its_adapters_do_not_reference_Driven(string assembly) =>
        Assert.DoesNotContain(Driven, References(assembly));

    [Fact]
    public void Driven_references_Core()
    {
        Assert.Contains("Chargehand", References(Driven));
        Assert.Contains("Chargehand.Driven", Assembly.Load("Chargehand.Server").GetReferencedAssemblies().Select(a => a.Name));
    }
}
