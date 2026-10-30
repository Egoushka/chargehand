using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>
/// Stability plan Task 2 (docs/plans/2026-09-30-stability-1.0.md): every enum that a frozen schema (result/v1,
/// run-status/v1, run-summary/v1, task-spec/v1, preset/v1) carries must take exactly the values its C# type can
/// produce, in both directions — a value the schema accepts but the enum cannot produce would fail to deserialize; a
/// value the enum can produce but the schema does not accept would fail validation (and the signature check, which
/// validates before signing in <see cref="ContractSchemas"/>'s callers).
///
/// request/v1's own enum-shaped fields (<c>context.delivery</c>, <c>driven.tasks[].size</c>, <c>driven.arm</c>) and
/// preset/v1's <c>permissions[].effect</c> are plain strings in C#, not <see cref="Chargehand.Contracts"/> enum
/// types (the plan's "What the extension API means here" section leaves the internal runtime types, such as
/// <c>PermissionEffect</c>, out of the frozen surface), so this class has no C# enum to diff them against.
/// </summary>
public class ContractParityTests
{
    private sealed record EnumSite(string Schema, string[] Path, Type EnumType);

    private static readonly EnumSite[] Sites =
    [
        new(ContractSchemas.Result, ["properties", "status"], typeof(ResultStatus)),
        new(ContractSchemas.Result, ["properties", "error", "properties", "code"], typeof(ErrorCode)),
        new(ContractSchemas.Result, ["properties", "claims", "items", "properties", "support"], typeof(ClaimSupport)),
        new(ContractSchemas.Result, ["properties", "evidence", "items", "properties", "kind"], typeof(EvidenceKind)),
        new(ContractSchemas.Result, ["$defs", "prompt_chain", "properties", "blocks", "items", "properties", "source"], typeof(BlockSource)),
        new(ContractSchemas.RunStatus, ["properties", "status"], typeof(RunState)),
        new(ContractSchemas.RunStatus, ["properties", "node_status"], typeof(ResultStatus)),
        new(ContractSchemas.RunStatus, ["properties", "event"], typeof(RunEventKind)),
        new(ContractSchemas.RunStatus, ["properties", "stage"], typeof(SessionStage)),
        new(ContractSchemas.RunSummary, ["properties", "status"], typeof(RunState)),
        new(ContractSchemas.TaskSpec, ["properties", "risk"], typeof(Risk)),
        new(ContractSchemas.TaskSpec, ["properties", "action"], typeof(TaskAction)),
        new(ContractSchemas.Preset, ["properties", "allowed_actions", "items"], typeof(TaskAction)),
        new(ContractSchemas.Preset, ["properties", "approval", "properties", "ask_above_risk"], typeof(Risk)),
    ];

    public static TheoryData<string, string[], Type> SiteCases()
    {
        var data = new TheoryData<string, string[], Type>();
        foreach (var site in Sites)
            data.Add(site.Schema, site.Path, site.EnumType);
        return data;
    }

    [Theory]
    [MemberData(nameof(SiteCases))]
    public void Schema_enum_and_the_C_sharp_enum_carry_the_same_values(string schema, string[] path, Type enumType)
    {
        using var doc = JsonDocument.Parse(ContractSchemas.Text(schema));
        var schemaValues = At(doc.RootElement, path).GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        var codeValues = Enum.GetNames(enumType).Select(n => JsonNamingPolicy.SnakeCaseLower.ConvertName(n)).ToHashSet(StringComparer.Ordinal);

        var location = $"{schema} #/{string.Join('/', path)}";
        var missingFromSchema = codeValues.Except(schemaValues).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var missingFromCode = schemaValues.Except(codeValues).OrderBy(v => v, StringComparer.Ordinal).ToList();
        Assert.True(missingFromSchema.Count == 0, $"{location}: {enumType.Name} can produce {string.Join(", ", missingFromSchema)}, not in the schema's enum");
        Assert.True(missingFromCode.Count == 0, $"{location}: the schema's enum accepts {string.Join(", ", missingFromCode)}, which {enumType.Name} has no member for");
    }

    private static JsonElement At(JsonElement root, string[] path)
    {
        var current = root;
        foreach (var segment in path)
            current = current.GetProperty(segment);
        return current;
    }
}
