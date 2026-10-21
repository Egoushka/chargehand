using Chargehand.Contracts;

namespace Chargehand.Kernel;

/// <summary>Where a run's output may go (ADR 0069): <c>local</c> keeps it on the machine, <c>push</c> is the behaviour before the field existed.</summary>
public static class DeliveryMode
{
    public const string Local = "local";
    public const string Push = "push";
}

/// <summary>
/// Decides the delivery mode of one repository from the request and the profile's <c>delivery.local_only</c> roots, and builds the refusal
/// every guard throws. The profile wins: a repository under a root is local whatever the request says, and a request that says
/// <c>push</c> for it is refused.
/// </summary>
public sealed class DeliveryPolicy
{
    private readonly string[] _roots;

    public DeliveryPolicy(IEnumerable<string>? localOnlyRoots) =>
        _roots = [.. (localOnlyRoots ?? []).Select(r => PathScope.RealPath(Expand(r)))];

    /// <summary>No roots: every repository takes the request's value.</summary>
    public static DeliveryPolicy None { get; } = new(null);

    /// <summary>The repository, given as git prints its top level, lies under a local-only root.</summary>
    public bool Forced(string repositoryTopLevel) => _roots.Length > 0 && _roots.Any(r => PathScope.Under(PathScope.RealPath(repositoryTopLevel), r));

    /// <summary><c>true</c> for local delivery. Throws <see cref="ErrorCode.InvalidRequest"/> for an unknown value and <see cref="ErrorCode.LocalOnly"/> for a <c>push</c> request on a forced repository.</summary>
    public bool IsLocal(string? requested, string repositoryTopLevel)
    {
        if (requested is not (null or DeliveryMode.Local or DeliveryMode.Push))
            throw new ChargehandException(ErrorCode.InvalidRequest, $"context.delivery must be \"local\" or \"push\", not \"{requested}\"", "Send local, push, or leave the field out.");
        if (!Forced(repositoryTopLevel))
            return requested == DeliveryMode.Local;
        return requested == DeliveryMode.Push ? throw Refused(repositoryTopLevel) : true;
    }

    /// <summary>The refusal for a repository that may not be pushed or sent to a forge.</summary>
    public static ChargehandException Refused(string repository, string what = "a push") =>
        new(ErrorCode.LocalOnly, $"{repository} is local-only (profile delivery.local_only or context.delivery \"local\"): {what} is not allowed",
            "Leave context.delivery out or set it to \"local\"; the work stays on this machine as a local branch.");

    private static string Expand(string root) =>
        root == "~" || root.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root[1..].TrimStart('/')) : root;
}

/// <summary>Path containment with symbolic links resolved, as git prints a top level (macOS: /var is /private/var).</summary>
public static class PathScope
{
    public static bool Under(string path, string root) =>
        path == root || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public static string RealPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetDirectoryName(full) is not { } parent)
            return full;
        var here = Path.Combine(RealPath(parent), Path.GetFileName(full));
        return Path.Exists(here) && File.ResolveLinkTarget(here, returnFinalTarget: true) is { } target ? RealPath(target.FullName) : here;
    }
}
