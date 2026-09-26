using Chargehand.Contracts;

namespace Chargehand.Prompts;

/// <summary>
/// Versioned, content-hashed prompt blocks from git (ADR 0007). Placement order: core system, preset, project
/// context, task (caller blocks included), retrieved facts. sha256 is over normalised UTF-8 (LF, no trailing spaces).
/// </summary>
public interface IPromptRegistry
{
    PromptBlock Get(string name);

    IReadOnlyList<PromptBlock> ForNode(string preset, string nodeKind);
}
