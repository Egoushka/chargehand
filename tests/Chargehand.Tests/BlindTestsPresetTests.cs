using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>Issue 789: the shipped blind-tests preset and the limits the blind sibling is given must say the same thing.</summary>
public class BlindTestsPresetTests
{
    [Fact]
    public void The_preset_file_agrees_with_the_limits_the_sibling_gets()
    {
        var yaml = File.ReadAllText(Repo.Path("presets", "blind-tests.yaml"));

        Assert.Contains($"name: {BlindLimits.PresetName}", yaml, StringComparison.Ordinal);
        Assert.Contains($"max_turns: {BlindLimits.MaxTurns}", yaml, StringComparison.Ordinal);
        Assert.Contains($"max_minutes: {BlindLimits.MaxMinutes}", yaml, StringComparison.Ordinal);
        Assert.Contains($"max_usd: {BlindLimits.BudgetUsd:0.00}", yaml, StringComparison.Ordinal);
    }
}
