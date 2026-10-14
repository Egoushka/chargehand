using System.Reflection;

namespace Chargehand.Tests;

/// <summary>ADR 0047: the orchestrator core ships without the driven infrastructure. A host with Core, Contracts and the adapters can answer read-only runs.</summary>
public class ArchitectureTests
{
    private const string Driven = "Chargehand.Driven";
    private const string Automation = "Chargehand.Automation";

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

    [Theory]
    [InlineData("Chargehand")]
    [InlineData("Chargehand.Contracts")]
    [InlineData("Chargehand.Mcp")]
    [InlineData("Chargehand.OpenCode")]
    [InlineData("Chargehand.ClaudeCode")]
    [InlineData("Chargehand.Driven")]
    public void Core_its_adapters_and_Driven_do_not_reference_Automation(string assembly) =>
        Assert.DoesNotContain(Automation, References(assembly));

    [Fact]
    public void Automation_references_Driven_and_the_server_references_Automation()
    {
        // The project reference: the compiled assembly drops it once no Driven type is used, as the port moves them into modules.
        Assert.Contains($@"..\{Driven}\{Driven}.csproj", File.ReadAllText(Repo.Path("src", Automation, $"{Automation}.csproj")));
        Assert.Contains(Automation, References("Chargehand.Server"));
    }

    [Fact]
    public void Driven_references_Core()
    {
        Assert.Contains("Chargehand", References(Driven));
        Assert.Contains("Chargehand.Driven", Assembly.Load("Chargehand.Server").GetReferencedAssemblies().Select(a => a.Name));
    }
}
