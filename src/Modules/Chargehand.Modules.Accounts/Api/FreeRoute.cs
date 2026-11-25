namespace Chargehand.Modules.Accounts.Api;

/// <summary>The task class of a run's node (spec 2026-11-25, D2). One run uses one preset and its nodes share a slot, so the class is decided per run.</summary>
public static class FreeTaskClass
{
    public const string SmallSlot = "provider/small-model";
    public const string CriticSlot = "provider/critic-model";
    public const string WorkerSlot = "provider/worker-model";

    /// <summary>The class for a node of <paramref name="preset"/> whose model placeholder is <paramref name="slot"/>; <paramref name="writes"/> is the node's own flag. Null: no class, so no free model.</summary>
    public static FreeTask? For(string preset, string slot, bool writes)
    {
        if (writes)
            return FreeTask.Edit;
        return slot switch
        {
            SmallSlot => FreeTask.Mechanical,
            CriticSlot => FreeTask.Critic,
            WorkerSlot => preset switch
            {
                "review" => FreeTask.Review,
                "design" or "split" => FreeTask.Plan,
                _ => FreeTask.Ask,
            },
            _ => null,
        };
    }
}

/// <summary>What a free-first lookup answered: the models to try in order, or why there are none.</summary>
public sealed record FreeOffer(FreeTask? Task, FreeTier? Tier, IReadOnlyList<string> Models, string? Reason)
{
    public static FreeOffer None(string reason, FreeTask? task = null, FreeTier? tier = null) => new(task, tier, [], reason);
}

/// <summary>The router of the maintainer's list, ported (spec 2026-11-25, D1): the tier of a repository, then the models of that tier graded for the class.</summary>
public static class FreeRoute
{
    /// <summary>Models <paramref name="tier"/> may use that are graded good or ok for <paramref name="task"/>: good before ok, file order inside a grade.
    /// A model whose provider the OpenCode server does not hold (<paramref name="providers"/>) drops out; ungraded and "no" never route.</summary>
    public static IReadOnlyList<string> Models(FreeModelCatalog catalog, FreeTier tier, FreeTask task, IReadOnlyCollection<string> providers) =>
        [.. catalog.Models
            .Select((m, order) => (m, order))
            .Where(x => (x.m.Tier == FreeTier.Private || tier == FreeTier.Public) && providers.Contains(x.m.Provider) && x.m.Grades.ContainsKey(task))
            .OrderBy(x => x.m.Grades[task]).ThenBy(x => x.order)
            .Select(x => x.m.Id)];

    /// <summary>Public only when GitHub says public; private, internal, unknown and a failed lookup are the private tier.</summary>
    public static FreeTier TierFor(RepositoryVisibility visibility) => visibility == RepositoryVisibility.Public ? FreeTier.Public : FreeTier.Private;

    /// <summary>The offer for a run on <paramref name="repositoryPath"/> (the checkout the server's own mapping names; <paramref name="forge"/> is its verified <c>owner/name</c> or null).</summary>
    public static async Task<FreeOffer> OfferAsync(FreeModelCatalog catalog, FreeFirstSettings settings, IRepositoryVisibility visibility,
        string? repositoryPath, string? forge, FreeTask? task, IReadOnlySet<string> excluded, CancellationToken ct, string? commit = null)
    {
        if (task is not { } t)
            return FreeOffer.None("the node has no task class");
        if (t == FreeTask.Edit)
            return FreeOffer.None("edit runs are not routed to free models", t);
        if (repositoryPath is null)
            return FreeOffer.None("the run names no repository", t);
        if (FreeFence.Refusal(repositoryPath, settings.Denied, commit) is { } fenced)
            return FreeOffer.None(fenced, t);
        var seen = forge is null ? RepositoryVisibility.Unknown : await visibility.LookupAsync(forge, ct).ConfigureAwait(false);
        var tier = TierFor(seen);
        var models = Models(catalog, tier, t, settings.ProviderList).Where(m => !excluded.Contains(m)).ToList();
        return models.Count == 0 ? FreeOffer.None("no free model is graded for this class and tier", t, tier) : new FreeOffer(t, tier, models, null);
    }
}

/// <summary>The profile's <c>free_first</c> block (spec 2026-11-25): where the mounted list is, which directory names never go to a free model, and which providers the OpenCode server holds.</summary>
public sealed record FreeFirstSettings(string? ModelsFile = null, IReadOnlyList<string>? DenyPathSegments = null, IReadOnlyList<string>? Providers = null, string? GithubTokenSecret = null)
{
    public IReadOnlyList<string> Denied => DenyPathSegments is { Count: > 0 } ? DenyPathSegments : FreeFence.DefaultDeny;

    public IReadOnlyList<string> ProviderList => Providers is { Count: > 0 } ? Providers : ["opencode"];

    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(ModelsFile))
            yield return "free_first.models_file is required";
        if (DenyPathSegments is { } deny && deny.Any(d => d.Contains('/', StringComparison.Ordinal) || d.Trim().Length == 0))
            yield return "free_first.deny_path_segments: a directory name each, no slash";
    }
}
