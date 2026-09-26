using System.Text.Json;

namespace Chargehand.Tests;

/// <summary>
/// Fails loudly when an OpenCode update (regenerated docs/opencode-openapi.json) drops an operation or a request
/// property the adapter relies on (docs/opencode-adapter-ops.json).
/// </summary>
public class OpenCodeSpecContractTests
{
    private static readonly JsonElement Spec = JsonDocument.Parse(File.ReadAllText(Repo.Path("docs", "opencode-openapi.json"))).RootElement;

    public static TheoryData<string, string[]> Operations()
    {
        var data = new TheoryData<string, string[]>();
        using var used = JsonDocument.Parse(File.ReadAllText(Repo.Path("docs", "opencode-adapter-ops.json")));
        foreach (var op in used.RootElement.GetProperty("operations").EnumerateArray())
            data.Add(op.GetProperty("op").GetString()!,
                op.TryGetProperty("body", out var b) ? b.EnumerateArray().Select(x => x.GetString()!).ToArray() : []);
        return data;
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void Adapter_operation_exists_with_its_request_properties(string op, string[] bodyProperties)
    {
        var (method, path) = (op[..op.IndexOf(' ')].ToLowerInvariant(), op[(op.IndexOf(' ') + 1)..]);
        Assert.True(Spec.GetProperty("paths").TryGetProperty(path, out var item), $"path missing: {path}");
        Assert.True(item.TryGetProperty(method, out var operation), $"method missing: {op}");
        if (bodyProperties.Length == 0)
            return;
        var schema = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        if (schema.TryGetProperty("$ref", out var r))
            schema = Spec.GetProperty("components").GetProperty("schemas").GetProperty(r.GetString()!.Split('/')[^1]);
        var props = schema.GetProperty("properties");
        foreach (var p in bodyProperties)
            Assert.True(props.TryGetProperty(p, out _), $"{op}: request property '{p}' missing");
    }

    [Fact]
    public void Pinned_spec_version_is_the_one_the_adapter_was_built_against()
    {
        Assert.Equal("0.0.1", Spec.GetProperty("info").GetProperty("version").GetString());
    }
}
