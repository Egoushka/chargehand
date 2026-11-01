namespace Chargehand.Modules.Handover.Api;

/// <summary>A pull request that adds a database migration (<c>db/migrations/*.sql</c>) is never merged or auto-merged by chargehand: the repository owner merges it. Path rules only; the folder may sit below
/// the repository root (a monorepo), and a changed or renamed migration counts like a new one.</summary>
public static class MigrationPaths
{
    /// <summary>The first changed path that is a migration, or null.</summary>
    public static string? Find(IEnumerable<string> changedPaths) => changedPaths.FirstOrDefault(IsMigration);

    public static bool IsMigration(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 3 && parts[^1].EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            && parts[^2].Equals("migrations", StringComparison.OrdinalIgnoreCase) && parts[^3].Equals("db", StringComparison.OrdinalIgnoreCase);
    }
}
