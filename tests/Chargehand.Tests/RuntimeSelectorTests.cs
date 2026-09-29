using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Which runtime to drive (ADR 0026, ADR 0032): a profile field or environment variable names one, then a
/// profile's only runtime block; otherwise PATH is probed.</summary>
public class RuntimeSelectorTests
{
    private static bool None(string binary) => false;

    [Fact]
    public void A_profile_field_wins_over_everything_else() =>
        Assert.Equal(RuntimeKind.ClaudeCode, RuntimeSelector.Select("claude_code", "opencode", _ => throw new InvalidOperationException("PATH should not be probed")));

    [Fact]
    public void An_environment_variable_wins_when_the_profile_names_none() =>
        Assert.Equal(RuntimeKind.Opencode, RuntimeSelector.Select(null, "opencode", _ => throw new InvalidOperationException("PATH should not be probed")));

    [Fact]
    public void A_single_runtime_block_in_the_profile_names_that_runtime() =>
        Assert.Equal(RuntimeKind.Opencode, RuntimeSelector.Select(null, null, _ => throw new InvalidOperationException("PATH should not be probed"), [RuntimeKind.Opencode]));

    [Fact]
    public void An_environment_variable_wins_over_a_runtime_block() =>
        Assert.Equal(RuntimeKind.ClaudeCode, RuntimeSelector.Select(null, "claude_code", _ => throw new InvalidOperationException("PATH should not be probed"), [RuntimeKind.Opencode]));

    [Fact]
    public void Two_runtime_blocks_name_neither_so_path_decides() =>
        Assert.Equal(RuntimeKind.ClaudeCode, RuntimeSelector.Select(null, null, binary => binary == "claude", [RuntimeKind.Opencode, RuntimeKind.ClaudeCode]));

    [Fact]
    public void Naming_an_unknown_runtime_is_an_invalid_request()
    {
        var e = Assert.Throws<ChargehandException>(() => RuntimeSelector.Select("something-else", null, None));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.NotNull(e.Action);
    }

    [Fact]
    public void Exactly_one_known_cli_on_path_is_selected() =>
        Assert.Equal(RuntimeKind.ClaudeCode, RuntimeSelector.Select(null, null, binary => binary == "claude"));

    [Fact]
    public void Zero_known_clis_on_path_is_runtime_unavailable()
    {
        var e = Assert.Throws<ChargehandException>(() => RuntimeSelector.Select(null, null, None));
        Assert.Equal(ErrorCode.RuntimeUnavailable, e.Code);
    }

    [Fact]
    public void More_than_one_known_cli_on_path_is_ambiguous_naming_both_no_silent_priority()
    {
        var e = Assert.Throws<ChargehandException>(() => RuntimeSelector.Select(null, null, _ => true));
        Assert.Equal(ErrorCode.RuntimeAmbiguous, e.Code);
        Assert.Contains("claude", e.Message, StringComparison.Ordinal);
        Assert.Contains("opencode", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void On_path_resolves_a_real_binary_by_walking_PATH_directories()
    {
        // "git" is a safe bet in any environment that can build and test this repository.
        Assert.True(RuntimeSelector.OnPath("git"));
        Assert.False(RuntimeSelector.OnPath("no-such-agent-cli-binary"));
    }
}
