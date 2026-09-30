using System.Text.Json.Serialization;

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
    Usage Usage,
    ResultError? Error = null,
    SignatureBlock? Signature = null);

public enum ResultStatus { Completed, NeedsInput, Failed, Denied }

/// <summary>Why a run failed, as a code a client can branch on (ADR 0022). Only on a failed result.</summary>
/// <param name="Action">What the user or client should do about it, e.g. the command that starts the runtime.</param>
public sealed record ResultError(ErrorCode Code, string Message, bool Retryable, string? Action = null);

public enum ErrorCode
{
    RuntimeUnavailable,
    RuntimeVersionMismatch,
    RuntimeAmbiguous,
    ProviderUnavailable,
    RateLimited,
    RepositoryNotAllowed,
    CheckoutInvalid,
    CheckoutHasSecrets,
    CostCapReached,
    DeadlineExceeded,
    InvalidResult,
    IntakeFailed,
    InvalidRequest,
    Internal,
    SandboxUnavailable,
    VerificationFailed,
    ContainerUnavailable,
    CredentialUnavailable,
    SessionFailed,
    SessionStalled,
    PushRejected,
    PrFailed,
    Cancelled,
    TasksIncomplete,
}

/// <param name="Support">Whether the text the claim cites supports it, as a model judged (ADR 0036); null when no check ran.</param>
public sealed record Claim(string Text, IReadOnlyList<string> Evidence, double Confidence, ClaimSupport? Support = null);

/// <summary>A claim the check found unsupported is not in <c>claims</c>: it moves to <c>open_questions</c>.</summary>
public enum ClaimSupport { Supported, Partial, Unchecked }

/// <summary>ES256 over the canonical result without this member (ADR 0036). <c>KeyId</c> is the first 16 hex characters of the
/// SHA-256 of the public key's SubjectPublicKeyInfo; <c>Value</c> is base64url.</summary>
public sealed record SignatureBlock(string Alg, string KeyId, string Value);

public sealed record Evidence(string Id, EvidenceKind Kind, string Locator, string? Commit = null, string? Sha256 = null);

public enum EvidenceKind { File, Diff, Url, SessionMessage, Commit, Input }

public sealed record Artifact(string Kind, string MediaType, string Sha256, string? Uri = null, string? Content = null);

/// <param name="Usd">Priced by the orchestrator's own table; null when any priced call's model had no price entry
/// (ADR 0026) — unknown cost, never a silent $0.</param>
public sealed record Usage(long Input, long Output, long CacheRead, long CacheWrite,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Usd);

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
