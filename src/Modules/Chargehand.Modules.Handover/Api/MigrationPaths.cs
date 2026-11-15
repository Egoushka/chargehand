namespace Chargehand.Modules.Handover.Api;

/// <summary>A pull request that adds a database migration is never merged or auto-merged by chargehand: the repository owner merges it. A migration is any file below a <c>Migrations</c> or <c>migrations</c> folder at any
/// depth (a monorepo, an EF Core project), or <c>db/migrations/*.sql</c>. Path rules only; a changed or renamed migration counts like a new one.</summary>
public static class MigrationPaths
{
    /// <summary>The first changed path that is a migration, or null.</summary>
    public static string? Find(IEnumerable<string> changedPaths) => changedPaths.FirstOrDefault(IsMigration);

    /// <summary><c>db/migrations/*.sql</c> is one case of the folder rule.</summary>
    public static bool IsMigration(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 2 && parts[..^1].Any(p => p.Equals("migrations", StringComparison.OrdinalIgnoreCase));
    }
}
