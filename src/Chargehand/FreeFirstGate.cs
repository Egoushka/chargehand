using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Research.Api;

namespace Chargehand;

/// <summary>Free models first (spec 2026-11-25, ADR 0103): the one place a run is offered the free models of its task class, and the one that says when a free attempt gives way to the next.
/// The repository is identified by the server's own mapping, never by the caller's text (<see cref="RepositoryIdentity"/>); the list is read from its mounted file on every run.</summary>
/// <param name="freeRuntime">The OpenCode runtime the free models run on; the profile's <c>opencode</c> block.</param>
public sealed class FreeFirstGate(Profile profile, FreeFirstSettings settings, IRepositoryVisibility visibility, IWorkerRuntime freeRuntime, Action<string>? warn = null)
{
    public IWorkerRuntime Runtime => freeRuntime;

    /// <summary>The free models to try for a run of <paramref name="preset"/> on <paramref name="repositoryPath"/>, minus <paramref name="tried"/>; or the reason there are none.</summary>
    public async Task<FreeOffer> OfferAsync(string preset, NodeKind kind, string? repositoryPath, string? commit, IReadOnlySet<string> tried, CancellationToken ct)
    {
        var task = FreeTaskClass.For(preset, kind.Model, kind.Writes);
        if (task is null || kind.Writes)
            return FreeOffer.None(task is null ? "the node has no task class" : "edit runs are not routed to free models", task);
        var (catalog, problem) = FreeModelCatalog.Load(settings.ModelsFile!);
        if (problem is not null)
        {
            warn?.Invoke(problem);
            return FreeOffer.None(problem, task);
        }
        var (forge, path) = await RepositoryIdentity.IdentifyAsync(profile, repositoryPath, ct).ConfigureAwait(false);
        return await FreeRoute.OfferAsync(catalog, settings, visibility, path ?? repositoryPath, forge, task, tried, ct, commit).ConfigureAwait(false);
    }

    /// <summary>The note the run record keeps about the offer.</summary>
    public static string Describe(FreeOffer offer) =>
        offer.Models.Count > 0 ? $"offered {offer.Task?.ToString().ToLowerInvariant()}/{offer.Tier?.ToString().ToLowerInvariant()}: {string.Join(", ", offer.Models)}"
            : $"none{(offer.Task is { } t ? $" ({t.ToString().ToLowerInvariant()})" : "")}: {offer.Reason}";

    /// <summary>Whether a free attempt's result hands the run to the next model: a rate limit, an unavailable provider or model, a worker that ended failed, or a result that failed its own checks
    /// all arrive as a failed result with one of these codes and nothing produced. Anything else (a cancel, a cost cap, a bad request or checkout, which every model would meet again) is the run's own end.</summary>
    public static bool FallsThrough(ResultContract result) =>
        result.Status == ResultStatus.Failed && result.Artifacts.Count == 0 && result.Error?.Code is
            ErrorCode.RateLimited or ErrorCode.ProviderUnavailable or ErrorCode.RuntimeUnavailable or ErrorCode.RuntimeVersionMismatch or ErrorCode.InvalidResult
            or ErrorCode.VerificationFailed or ErrorCode.SessionFailed or ErrorCode.SessionStalled or ErrorCode.DeadlineExceeded or ErrorCode.IntakeFailed or ErrorCode.Internal;

    public static string Outcome(ResultContract result) =>
        result.Status == ResultStatus.Failed ? $"failed:{Snake(result.Error?.Code)}" : result.Status.ToString().ToLowerInvariant();

    /// <summary>What went wrong in a failed attempt, scrubbed of keys and cut short; null when it did not fail.</summary>
    public static string? Detail(ResultContract result)
    {
        if (result.Status != ResultStatus.Failed || result.Error?.Message is not { Length: > 0 } message)
            return null;
        var clean = Kernel.ChargehandException.Scrub(message);
        return clean.Length > 200 ? clean[..200] : clean;
    }

    private static string Snake(ErrorCode? code) =>
        code is null ? "unknown" : string.Concat(code.Value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
