namespace Chargehand.Tests;

/// <summary>ADR 0078: mise reads pin files at the repository root only, so the root <c>mise.toml</c> repeats the console's Node pin; the two must not drift.</summary>
public class MisePinTests
{
    [Fact]
    public void The_root_mise_toml_pins_the_node_of_web_console_nvmrc()
    {
        var nvmrc = File.ReadAllText(Path.Combine(Repo.Root, "web", "console", ".nvmrc")).Trim();
        var pin = File.ReadAllLines(Path.Combine(Repo.Root, "mise.toml")).Single(l => l.StartsWith("node = ", StringComparison.Ordinal));
        Assert.Equal($"node = \"{nvmrc}\"", pin);
    }
}
