namespace Chargehand.Enhancement;

/// <summary>
/// What chargehand may tell a prompt enhancer besides the prompt (ADR 0040): these four fields and nothing else, so file
/// contents, secrets and other runs cannot be sent by mistake.
/// </summary>
public sealed record EnhanceContext(string? Repository = null, string? Commit = null, string? TaskKind = null, string? Client = "chargehand");

/// <summary>An enhancer's answer. <see cref="Rewrite"/> is null for a pass-through, so a client shows no diff and sends <see cref="Original"/>.</summary>
/// <param name="Rewrite">Text for the user to accept or reject. Never instructions to chargehand (ADR 0040).</param>
/// <param name="Reason">Why the enhancer answered as it did, or why chargehand fell back to the original.</param>
/// <param name="RequestId">What <see cref="IPromptEnhancer.FeedbackAsync"/> needs; null when the enhancer gave none.</param>
public sealed record Enhanced(
    string Original, string? Rewrite, string Reason, string? RequestId = null, string? TemplateId = null, string? TemplateVersion = null, bool HeldOut = false)
{
    public bool Changed => Rewrite is not null;

    /// <summary>The original prompt, unchanged, with the reason it is all there is.</summary>
    public static Enhanced Unchanged(string original, string reason, string? requestId = null) => new(original, null, reason, requestId);
}

/// <summary>What happened after an enhance call; null where chargehand does not know.</summary>
public sealed record EnhanceOutcome(bool? RewriteAccepted = null, bool? ModelOverridden = null, double? Score = null, decimal? CostUsd = null, string? Model = null);

/// <summary>
/// A prompt enhancer, the <c>prompt-enhancer</c> extension category (ADR 0040): it owns the user's prompts, chargehand
/// sends it one and gives back the outcome. The category's default is none.
/// </summary>
public interface IPromptEnhancer
{
    Task<Enhanced> EnhanceAsync(string prompt, EnhanceContext context, CancellationToken ct);

    Task FeedbackAsync(string requestId, EnhanceOutcome outcome, CancellationToken ct);
}
