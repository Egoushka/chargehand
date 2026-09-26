using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Tests;

public class SchemaFixtureTests
{
    public static TheoryData<string, string> Fixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in Directory.GetFiles(Repo.Path("schemas"), "*.json", SearchOption.AllDirectories)
                     .Where(f => f.Contains($"{System.IO.Path.DirectorySeparatorChar}examples{System.IO.Path.DirectorySeparatorChar}")))
        {
            var rel = System.IO.Path.GetRelativePath(Repo.Path("schemas"), file).Split(System.IO.Path.DirectorySeparatorChar);
            data.Add($"{rel[0]}/{rel[1]}", System.IO.Path.GetRelativePath(Repo.Root, file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_matches_its_expected_validity(string schema, string fixture)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Repo.Path(fixture)));
        var errors = ContractSchemas.Validate(schema, doc.RootElement);
        var expectValid = System.IO.Path.GetFileName(fixture).StartsWith("valid-", StringComparison.Ordinal);
        Assert.True(expectValid == (errors.Count == 0), $"{fixture}: expected {(expectValid ? "valid" : "invalid")}; errors: {string.Join("; ", errors)}");
    }

    [Theory]
    [InlineData("request/v1", "urn:chargehand:schema:request:v1")]
    [InlineData("task-spec/v1", "urn:chargehand:schema:task-spec:v1")]
    [InlineData("result/v1", "urn:chargehand:schema:result:v1")]
    [InlineData("preset/v1", "urn:chargehand:schema:preset:v1")]
    [InlineData("run-status/v1", "urn:chargehand:schema:run-status:v1")]
    public void Embedded_schema_carries_its_major_in_the_id(string name, string id)
    {
        using var doc = JsonDocument.Parse(ContractSchemas.Text(name));
        Assert.Equal(id, doc.RootElement.GetProperty("$id").GetString());
    }
}
