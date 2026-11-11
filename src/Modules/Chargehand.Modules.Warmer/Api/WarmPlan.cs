namespace Chargehand.Modules.Warmer.Api;

/// <summary>One restore a checkout needs. A null <paramref name="File"/> is a check made in this process: done means hit, otherwise it is missing.</summary>
/// <param name="Done">True when the step's result already exists, so running it again is not needed.</param>
public sealed record WarmStep(string Name, string? File, IReadOnlyList<string> Args, string WorkDir, Func<bool> Done);

/// <summary>What to restore in a checkout, decided from the files in it: the top level and each direct subdirectory.</summary>
public static class WarmPlan
{
    private static readonly string[] Skipped = ["node_modules", ".git"];

    public static IReadOnlyList<WarmStep> For(string dir)
    {
        var steps = new List<WarmStep>();
        if (Dotnet(dir) is { } dotnet)
            steps.Add(dotnet);
        var playwright = false;
        foreach (var d in Folders(dir))
        {
            if (File.Exists(Path.Combine(d, ".pnpmfile.cjs")))
                continue;
            if (File.Exists(Path.Combine(d, "pnpm-lock.yaml")))
                steps.Add(Node("pnpm-install", "pnpm", ["install", "--prefer-offline", "--frozen-lockfile", "--ignore-scripts", "--ignore-pnpmfile"], d, ".modules.yaml"));
            else if (File.Exists(Path.Combine(d, "package-lock.json")))
                steps.Add(Node("npm-ci", "npm", ["ci", "--prefer-offline", "--ignore-scripts"], d, ".package-lock.json"));
            else
                continue;
            playwright |= UsesPlaywright(d);
        }
        if (playwright)
            steps.Add(new WarmStep("playwright-browsers", null, [], dir, BrowsersInstalled));
        return steps;
    }

    private static WarmStep? Dotnet(string dir)
    {
        var target = Pick(dir, "*.slnx") ?? Pick(dir, "*.sln") ?? Pick(dir, "*.csproj");
        return target is null
            ? null
            : new WarmStep("dotnet-restore", "dotnet", ["restore", target, "--disable-build-servers"], dir, () => HasAssets(dir, 4));
    }

    private static string? Pick(string dir, string pattern) =>
        Directory.EnumerateFiles(dir, pattern).Select(f => "./" + Path.GetFileName(f)).Order(StringComparer.Ordinal).FirstOrDefault();

    private static bool HasAssets(string dir, int depth)
    {
        if (File.Exists(Path.Combine(dir, "obj", "project.assets.json")))
            return true;
        return depth > 0 && Children(dir).Any(d => HasAssets(d, depth - 1));
    }

    private static WarmStep Node(string name, string file, string[] args, string dir, string marker) =>
        new(name, file, args, dir, () => File.Exists(Path.Combine(dir, "node_modules", marker)));

    private static IEnumerable<string> Folders(string dir) => new[] { dir }.Concat(Children(dir));

    private static IEnumerable<string> Children(string dir) =>
        Directory.EnumerateDirectories(dir)
            .Where(d => !Skipped.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.') && new DirectoryInfo(d).LinkTarget is null)
            .Order(StringComparer.Ordinal);

    private static bool UsesPlaywright(string dir) =>
        File.Exists(Path.Combine(dir, "package.json")) && File.ReadAllText(Path.Combine(dir, "package.json")).Contains("\"@playwright/test\"", StringComparison.Ordinal);

    private static bool BrowsersInstalled()
    {
        var path = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        path = string.IsNullOrEmpty(path) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright") : path;
        return Directory.Exists(path) && Directory.EnumerateDirectories(path, "chromium-*").Any();
    }
}
