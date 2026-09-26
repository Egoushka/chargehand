namespace Chargehand.Contracts;

/// <summary>result/v1: what a node returns, and what a run returns to its caller.</summary>
public sealed record ResultContract(
    string ContractVersion,
    string TaskId,
    string NodeId,
    string TraceId,
    PromptChain PromptChain,
    ResultStatus Status,
    string Summary,
    IReadOnlyList<Claim> Claims,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<Artifact> Artifacts,
    IReadOnlyList<string> OpenQuestions,
    double Confidence,
    Usage Usage);

public enum ResultStatus { Completed, NeedsInput, Failed, Denied }

public sealed record Claim(string Text, IReadOnlyList<string> Evidence, double Confidence);

public sealed record Evidence(string Id, EvidenceKind Kind, string Locator, string? Commit = null, string? Sha256 = null);

public enum EvidenceKind { File, Diff, Url, SessionMessage, Commit, Input }

public sealed record Artifact(string Kind, string MediaType, string Sha256, string? Uri = null, string? Content = null);

public sealed record Usage(long Input, long Output, long CacheRead, long CacheWrite, decimal Usd);

public sealed record PromptChain(IReadOnlyList<ChainBlock> Blocks, AsSent AsSent);

public sealed record ChainBlock(string Name, string Version, string Sha256, BlockSource Source);

public enum BlockSource { Registry, Caller, Runtime }

/// <summary>The context OpenCode actually sent, which the orchestrator does not hash itself.</summary>
public sealed record AsSent(
    string OpencodeVersion,
    string Agent,
    string Model,
    string Date,
    string? BasePromptVariant = null,
    string? InstructionFilesSha256 = null,
    string? ToolsSha256 = null);
