namespace Chargehand.Tests;

internal static class Repo
{
    public static readonly string Root = FindRoot();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Chargehand.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (Chargehand.slnx) not found.");
    }
}
