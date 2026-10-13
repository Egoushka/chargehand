using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>ADR 0060: the source-generated contract JSON writes and reads exactly what the reflection options do, so run logs and clients see no change.</summary>
public class ContractJsonContextTests
{
    public static TheoryData<string> ValidExamples() => new(
        Directory.GetFiles(Repo.Path("schemas"), "valid-*.json", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(Repo.Root, f))
            .Where(f => TypeOf(f) is not null));

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void An_example_round_trips_to_the_same_bytes_either_way(string example)
    {
        var json = File.ReadAllText(System.IO.Path.Combine(Repo.Root, example));
        var info = TypeOf(example)!;

        var reflected = JsonSerializer.Deserialize(json, info.Type, ContractJson.Options);
        var generated = JsonSerializer.Deserialize(json, info);

        Assert.Equal(JsonSerializer.Serialize(reflected, info.Type, ContractJson.Options), JsonSerializer.Serialize(generated, info));
        Assert.Equal(JsonSerializer.Serialize(reflected, info.Type, ContractJson.Options), JsonSerializer.Serialize(reflected, info));
    }

    [Fact]
    public void Every_contract_enum_is_written_as_snake_case() =>
        Assert.Equal("\"needs_input\"", JsonSerializer.Serialize(RunState.NeedsInput, ContractJsonContext.Default.Options));

    private static JsonTypeInfo? TypeOf(string example) => example.Replace('\\', '/') switch
    {
        var f when f.Contains("/request/", StringComparison.Ordinal) => ContractJsonContext.Default.RunRequest,
        var f when f.Contains("/result/", StringComparison.Ordinal) => ContractJsonContext.Default.ResultContract,
        var f when f.Contains("/run-status/", StringComparison.Ordinal) => ContractJsonContext.Default.RunStatus,
        var f when f.Contains("/run-summary/", StringComparison.Ordinal) => ContractJsonContext.Default.RunSummary,
        var f when f.Contains("/task-spec/", StringComparison.Ordinal) => ContractJsonContext.Default.TaskSpec,
        _ => null,
    };
}
