using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>The C# types must round-trip every valid fixture without losing schema validity.</summary>
public class ContractTypeTests
{
    [Theory]
    [InlineData("result/v1", typeof(ResultContract))]
    [InlineData("request/v1", typeof(RunRequest))]
    [InlineData("task-spec/v1", typeof(TaskSpec))]
    public void Valid_fixtures_round_trip_through_the_types(string schema, Type type)
    {
        var parts = schema.Split('/');
        foreach (var file in Directory.GetFiles(Repo.Path("schemas", parts[0], parts[1], "examples"), "valid-*.json"))
        {
            var typed = JsonSerializer.Deserialize(File.ReadAllText(file), type, ContractJson.Options)!;
            var element = JsonSerializer.SerializeToElement(typed, type, ContractJson.Options);
            var errors = ContractSchemas.Validate(schema, element);
            Assert.True(errors.Count == 0, $"{System.IO.Path.GetFileName(file)}: {string.Join("; ", errors)}");
        }
    }
}
