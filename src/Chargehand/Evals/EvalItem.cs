using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;

namespace Chargehand.Evals;

/// <summary>
/// One eval cell (ADR 0019): a preset and node kind, or intake alone; its Langfuse dataset; the files whose change it
/// gates; its quality and cost tolerances. Cells live in the public <c>evals/cells.json</c>; their items do not.
/// </summary>
/// <param name="Kind">worker, draft or intake: what runs and how it is scored.</param>
/// <param name="Preset">The preset worker and draft items run under; intake items keep their own.</param>
/// <param name="QualityTolerance">T: the mean paired quality drop (0–1 scale) a change may cause.</param>
/// <param name="CostTolerance">C: the relative cost rise a change may cause (0.15 = +15%).</param>
public sealed record EvalCell(string Name, string Dataset, string Kind, string? Preset, IReadOnlyList<string> Files, double QualityTolerance, double CostTolerance,
    int MinItems = 8)
{
    public static IReadOnlyList<EvalCell> Load(string path) => JsonSerializer.Deserialize<CellFile>(File.ReadAllText(path), Profile.Json)!.Cells;

    private sealed record CellFile(IReadOnlyList<EvalCell> Cells);
}

/// <summary>An eval item: a request and what a good result contains, reviewed by hand before it is pushed.</summary>
public sealed record EvalItem(string Id, RunRequest Request, EvalExpected Expected, IReadOnlyDictionary<string, string>? Metadata = null);

/// <param name="ReferenceFiles">worker: repository files a good answer cites.</param>
/// <param name="Action">intake: the action intake should choose.</param>
/// <param name="Draft">draft: what the draft must keep to.</param>
public sealed record EvalExpected(IReadOnlyList<string>? ReferenceFiles = null, string? Action = null, DraftExpectation? Draft = null);

/// <param name="RequiredInputs">Input ids the draft's claims must cite.</param>
/// <param name="Banned">Phrases the draft must not contain (case-insensitive).</param>
public sealed record DraftExpectation(int MaxChars, IReadOnlyList<string>? RequiredInputs = null, IReadOnlyList<string>? Banned = null);
