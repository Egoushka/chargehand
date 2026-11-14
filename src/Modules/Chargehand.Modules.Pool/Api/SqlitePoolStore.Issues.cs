using System.Globalization;

namespace Chargehand.Modules.Pool.Api;

public sealed partial class SqlitePoolStore
{
    // Version 2: a v1 file gets the columns once, in the same transaction that raises user_version.
    private const string AddIssueAndSize = "ALTER TABLE items ADD COLUMN issue INTEGER; ALTER TABLE items ADD COLUMN size TEXT;";

    public Task<bool> HasOpenItemAsync(string repository, int issue, CancellationToken ct)
    {
        using var db = Connect(connection);
        using var cmd = Command(db, null, "SELECT COUNT(*) FROM items WHERE repository = $repo AND issue = $issue AND status <> 'done';",
            ("$repo", repository), ("$issue", issue));
        return Task.FromResult(Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0);
    }
}
