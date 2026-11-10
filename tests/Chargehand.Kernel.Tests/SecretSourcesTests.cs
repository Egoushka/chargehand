using System.Text.Json;

namespace Chargehand.Kernel.Tests;

/// <summary>ADR 0026, ADR 0060: the profile's <c>secrets</c> and <c>telemetry</c> bind without reflection, and the sources resolve an item in order.</summary>
public class SecretSourcesTests
{
    [Fact]
    public void Secrets_and_telemetry_bind_from_the_profile_shape()
    {
        var sources = JsonSerializer.Deserialize("""[{ "env": true }, { "command": ["security", "find-generic-password", "-s", "{item}", "-w"] }]""",
            KernelSettingsJsonContext.Default.IReadOnlyListSecretSource)!;
        var telemetry = JsonSerializer.Deserialize("""{ "otlp_endpoint": "https://collector.example/v1/traces", "public_key_secret": "pk", "secret_key_secret": "sk", "usage_on_spans": true }""",
            KernelSettingsJsonContext.Default.TelemetrySettings)!;

        Assert.Equal([new SecretSource(Env: true), new SecretSource(Command: ["security", "find-generic-password", "-s", "{item}", "-w"])], sources, SourceEquality.Instance);
        Assert.Equal(new TelemetrySettings("https://collector.example/v1/traces", "pk", "sk", UsageOnSpans: true), telemetry);
    }

    [Fact]
    public void The_first_source_that_resolves_the_item_wins_and_a_command_sees_the_item()
    {
        var item = $"kernel-tests-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(item.ToUpperInvariant().Replace('-', '_'), "from-env");
        try
        {
            Assert.Equal("from-env", SecretSources.Resolve(null, item));
            Assert.Equal($"from-command-{item}", SecretSources.Resolve([new SecretSource(Command: ["sh", "-c", "printf %s from-command-$0", "{item}"]), new SecretSource(Env: true)], item));
        }
        finally
        {
            Environment.SetEnvironmentVariable(item.ToUpperInvariant().Replace('-', '_'), null);
        }
    }

    [Fact]
    public void No_source_resolving_the_item_is_an_error_that_names_it()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SecretSources.Resolve([new SecretSource(Command: ["sh", "-c", "exit 1"])], "absent-item"));

        Assert.Contains("'absent-item'", e.Message);
    }

    [Fact]
    public void A_hanging_command_is_killed_at_the_timeout_and_the_next_source_is_tried()
    {
        var item = $"kernel-tests-after-a-hang-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(item.ToUpperInvariant().Replace('-', '_'), "from-second");
        try
        {
            Assert.Equal("from-second", SecretSources.Resolve([new SecretSource(Command: ["sleep", "30"]), new SecretSource(Env: true)], item, TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(item.ToUpperInvariant().Replace('-', '_'), null);
        }
        Assert.Equal(TimeSpan.FromSeconds(15), SecretSources.DefaultCommandTimeout);
    }

    private sealed class SourceEquality : IEqualityComparer<SecretSource>
    {
        public static readonly SourceEquality Instance = new();

        public bool Equals(SecretSource? x, SecretSource? y) => x is not null && y is not null && x.Env == y.Env && (x.Command ?? []).SequenceEqual(y.Command ?? []);

        public int GetHashCode(SecretSource obj) => obj.Env.GetHashCode();
    }
}
