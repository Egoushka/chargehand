using System.Text.RegularExpressions;

namespace Chargehand.Modules.Warmer.Api;

/// <summary>One restore a checkout needs. A null <paramref name="File"/> is a check made in this process: done means hit, otherwise it is missing.</summary>
/// <param name="Done">True when the step's result already exists, so running it again is not needed.</param>
/// <param name="Stages">True when the step writes NuGet packages that must be verified before they reach the shared cache.</param>
/// <param name="Staging">The per-run folder a staged step writes its packages to; set by the warmer, never by the plan.</param>
/// <param name="Scope">The checkout's top-level folder name, so a step's cache can be kept to that one repository; set by the warmer, never by the plan.</param>
public sealed record WarmStep(string Name, string? File, IReadOnlyList<string> Args, string WorkDir, Func<bool> Done, bool Stages = false, string? Staging = null, string? Scope = null);

/// <summary>What to restore in a checkout, decided from the files in it: the top level and each direct subdirectory.</summary>
public static partial class WarmPlan
{
    private static readonly string[] Skipped = ["node_modules", ".git"];

    private static readonly string[] PnpmConfigFiles = [".npmrc", "pnpm-workspace.yaml"];

    // --ignore-pnpmfile covers only the local .pnpmfile.cjs; a hook named in the config, a global one or a config dependency would still run code.
    // Any mention of a key counts, wherever it sits (a flow map, a YAML `?` key), so a form the match did not foresee refuses rather than warms.
    [GeneratedRegex("""(?<![\w-])(pnpmfile|global-pnpmfile|globalPnpmfile|configDependencies|config-dependencies)(?![\w-])""", RegexOptions.IgnoreCase)]
    private static partial Regex PnpmHookKey();

    public static IReadOnlyList<WarmStep> For(string dir)
    {
        var steps = new List<WarmStep>();
        if (Dotnet(dir) is { } dotnet)
            steps.Add(dotnet);
        var playwright = false;
        // pnpm reads the workspace root's config from any package below it, so a hooked root stops every pnpm folder.
        var rootHooked = PnpmHooked(dir);
        foreach (var d in Folders(dir))
        {
            var pnpm = File.Exists(Path.Combine(d, "pnpm-lock.yaml"));
            if (PnpmHooked(d) || (pnpm && rootHooked))
                continue;
            if (pnpm)
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

    private static bool PnpmHooked(string dir) =>
        File.Exists(Path.Combine(dir, ".pnpmfile.cjs")) || File.Exists(Path.Combine(dir, ".pnpmfile.mjs"))
        || PnpmConfigFiles.Select(f => Path.Combine(dir, f)).Any(f => File.Exists(f) && PnpmHookKey().IsMatch(File.ReadAllText(f)));

    private static WarmStep? Dotnet(string dir)
    {
        var target = Pick(dir, "*.slnx") ?? Pick(dir, "*.sln") ?? Pick(dir, "*.csproj");
        return target is null
            ? null
            : new WarmStep("dotnet-restore", "dotnet", ["restore", target, "--disable-build-servers"], dir, () => HasAssets(dir, 4), Stages: true);
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
