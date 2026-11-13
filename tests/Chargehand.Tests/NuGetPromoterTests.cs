using System.IO.Compression;
using Chargehand.Warming;

namespace Chargehand.Tests;

public sealed class NuGetPromoterTests : IDisposable
{
    private const string Signed = "SIGNED";
    private readonly TempDir temp = new();
    private readonly List<IReadOnlyList<string>> calls = [];
    private readonly List<string> feedSeen = [];
    private string? configSeen;

    private string Staging => Path.Combine(temp.Path, "staging");

    private string Shared => Path.Combine(temp.Path, "shared");

    private string WorkRoot => Path.Combine(temp.Path, "work");

    public void Dispose() => temp.Dispose();

    private string Stage(string id, string version, string content)
    {
        var dir = Path.Combine(Staging, id, version);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{id}.{version}.nupkg");
        File.WriteAllText(file, content);
        return dir;
    }

    /// <summary>A stand-in for dotnet: <c>verify</c> passes a file containing SIGNED; <c>restore</c> "extracts" every .nupkg in the feed beside the config into --packages.</summary>
    private Task<(int ExitCode, string Output)> Fake(IReadOnlyList<string> args, string workDir, CancellationToken ct)
    {
        calls.Add(args);
        if (args[1] == "verify")
            return Task.FromResult((args.Skip(2).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).All(f => File.ReadAllText(f) == Signed) ? 0 : 1, ""));
        var config = args[args.ToList().IndexOf("--configfile") + 1];
        configSeen = File.ReadAllText(config);
        var packages = args[args.ToList().IndexOf("--packages") + 1];
        var feed = Path.Combine(Path.GetDirectoryName(config)!, "feed");
        foreach (var f in Directory.GetFiles(feed, "*", SearchOption.AllDirectories))
        {
            feedSeen.Add(Path.GetRelativePath(feed, f));
            var parts = Path.GetFileNameWithoutExtension(f).Split('.', 2);
            var dir = Path.Combine(packages, parts[0], parts[1]);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "extracted.marker"), "ok");
        }
        return Task.FromResult((0, ""));
    }

    private NuGetPromoter Promoter(IReadOnlyList<string>? extra = null, DotnetRunner? dotnet = null) => new(Shared, WorkRoot, extra ?? [], dotnet ?? Fake);

    [Fact]
    public async Task A_signed_package_is_extracted_into_the_shared_cache_from_the_private_feed()
    {
        Stage("pkg", "1.0.0", Signed);
        var result = await Promoter().PromoteAsync(Staging, default);

        Assert.Equal(["pkg/1.0.0"], result.Promoted);
        Assert.Empty(result.Skipped);
        Assert.True(File.Exists(Path.Combine(Shared, "pkg", "1.0.0", "extracted.marker")));
        Assert.Equal(["pkg.1.0.0.nupkg"], feedSeen);
    }

    [Fact]
    public async Task An_unsigned_package_is_not_promoted_and_nothing_is_restored()
    {
        Stage("pkg", "1.0.0", "UNSIGNED");
        var result = await Promoter().PromoteAsync(Staging, default);

        Assert.Empty(result.Promoted);
        Assert.Equal(["pkg/1.0.0"], result.Skipped);
        Assert.False(Directory.Exists(Shared));
        Assert.DoesNotContain(calls, c => c[0] == "restore");
    }

    [Fact]
    public async Task A_file_planted_in_staging_next_to_a_valid_package_does_not_reach_the_feed_or_the_shared_cache()
    {
        var dir = Stage("pkg", "1.0.0", Signed);
        File.WriteAllText(Path.Combine(dir, "evil.dll"), "payload");
        File.WriteAllText(Path.Combine(dir, "pkg.nuspec"), "<evil/>");
        Directory.CreateDirectory(Path.Combine(dir, "lib", "net10.0"));
        File.WriteAllText(Path.Combine(dir, "lib", "net10.0", "pkg.dll"), "payload");
        File.WriteAllText(Path.Combine(Staging, "pkg", "stray.txt"), "payload");
        Directory.CreateDirectory(Path.Combine(Staging, "planted", "1.0.0"));
        File.WriteAllText(Path.Combine(Staging, "planted", "1.0.0", "planted.dll"), "payload");

        var result = await Promoter().PromoteAsync(Staging, default);

        Assert.Equal(["pkg/1.0.0"], result.Promoted);
        Assert.Equal(["pkg.1.0.0.nupkg"], feedSeen);
        Assert.Equal(["extracted.marker"], Directory.GetFiles(Shared, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public async Task A_planted_nupkg_with_an_unsigned_payload_is_skipped_while_its_signed_neighbour_is_promoted()
    {
        Stage("good", "1.0.0", Signed);
        Stage("bad", "2.0.0", "TAMPERED");
        var result = await Promoter().PromoteAsync(Staging, default);

        Assert.Equal(["good/1.0.0"], result.Promoted);
        Assert.Equal(["bad/2.0.0"], result.Skipped);
        Assert.Equal(["good.1.0.0.nupkg"], feedSeen);
        Assert.False(Directory.Exists(Path.Combine(Shared, "bad")));
    }

    [Fact]
    public async Task A_symlinked_nupkg_and_hostile_names_are_ignored()
    {
        File.WriteAllText(Path.Combine(temp.Path, "outside.nupkg"), Signed);
        Directory.CreateDirectory(Path.Combine(Staging, "link", "1.0.0"));
        File.CreateSymbolicLink(Path.Combine(Staging, "link", "1.0.0", "link.1.0.0.nupkg"), Path.Combine(temp.Path, "outside.nupkg"));
        Stage("a b", "1.0.0", Signed);
        Stage("..evil", "1.0.0", Signed);
        Stage("-rf", "1.0.0", Signed);
        Stage("ok", "1.0.0/../x", Signed);
        Stage("ok2", "1.0", Signed);
        File.Delete(Path.Combine(Staging, "ok2", "1.0", "ok2.1.0.nupkg"));
        File.WriteAllText(Path.Combine(Staging, "ok2", "1.0", "other.nupkg"), Signed);

        var result = await Promoter().PromoteAsync(Staging, default);

        Assert.Empty(result.Promoted);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task The_trusted_fingerprints_reach_verify_and_the_require_mode_config()
    {
        Stage("pkg", "1.0.0", Signed);
        var extra = new string('a', 64);
        await Promoter([extra]).PromoteAsync(Staging, default);

        var verify = calls.First(c => c[1] == "verify");
        foreach (var fp in NuGetPromoter.DefaultFingerprints)
            Assert.Contains(fp, verify);
        Assert.Contains(extra.ToUpperInvariant(), verify);
        Assert.Contains("signatureValidationMode\" value=\"require\"", configSeen);
        Assert.Contains("<clear />", configSeen);
        Assert.Contains(NuGetPromoter.DefaultFingerprints[0], configSeen);
        Assert.Contains($"<author name=\"extra1\">", configSeen);
        Assert.Contains(extra.ToUpperInvariant(), configSeen);
    }

    [Fact]
    public void An_invalid_trusted_signer_fails_at_startup_instead_of_being_dropped()
    {
        var bad = "not-a-fingerprint-" + new string('z', 40);
        var ex = Assert.Throws<ArgumentException>(() => Promoter([new string('a', 64), bad]));
        Assert.Contains("nuget_trusted_signers", ex.Message);
        Assert.Contains("entry 2", ex.Message);
        Assert.DoesNotContain(bad, ex.Message);
    }

    [Fact]
    public async Task A_failed_extraction_promotes_nothing()
    {
        Stage("pkg", "1.0.0", Signed);
        var result = await Promoter(dotnet: async (args, dir, ct) => args[0] == "restore" ? (1, "boom") : await Fake(args, dir, ct)).PromoteAsync(Staging, default);

        Assert.Empty(result.Promoted);
        Assert.Equal(["pkg/1.0.0"], result.Skipped);
    }

    [Fact]
    public async Task The_work_folder_is_deleted_and_an_empty_staging_folder_runs_nothing()
    {
        Stage("pkg", "1.0.0", Signed);
        await Promoter().PromoteAsync(Staging, default);
        Assert.Empty(Directory.EnumerateFileSystemEntries(WorkRoot));

        var empty = await Promoter().PromoteAsync(Path.Combine(temp.Path, "nothing"), default);
        Assert.Empty(empty.Promoted);
    }

    [Fact]
    public async Task The_extraction_restore_never_evaluates_a_repository_authored_msbuild_file()
    {
        Stage("pkg", "1.0.0", Signed);
        string? props = null, targets = null, packagesProps = null;
        Task<(int ExitCode, string Output)> Capture(IReadOnlyList<string> args, string workDir, CancellationToken ct)
        {
            if (args[0] == "restore")
            {
                props = File.ReadAllText(Path.Combine(workDir, "Directory.Build.props"));
                targets = File.ReadAllText(Path.Combine(workDir, "Directory.Build.targets"));
                packagesProps = File.ReadAllText(Path.Combine(workDir, "Directory.Packages.props"));
            }
            return Fake(args, workDir, ct);
        }

        await Promoter(dotnet: Capture).PromoteAsync(Staging, default);

        Assert.Equal("<Project />", props);
        Assert.Equal("<Project />", targets);
        Assert.Equal("<Project />", packagesProps);
    }

    [Fact]
    public async Task Real_dotnet_rejects_an_unsigned_package_and_nothing_reaches_the_shared_cache()
    {
        var dir = Path.Combine(Staging, "unsigned.pkg", "1.0.0");
        Directory.CreateDirectory(dir);
        using (var zip = ZipFile.Open(Path.Combine(dir, "unsigned.pkg.1.0.0.nupkg"), ZipArchiveMode.Create))
        {
            var nuspec = zip.CreateEntry("unsigned.pkg.nuspec");
            await using (var w = nuspec.Open())
            await using (var sw = new StreamWriter(w))
                await sw.WriteAsync("<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>unsigned.pkg</id><version>1.0.0</version><authors>x</authors><description>x</description></metadata></package>");
            zip.CreateEntry("lib/net10.0/_._");
        }

        var result = await new NuGetPromoter(Shared, WorkRoot, [], DotnetProcess.RunAsync).PromoteAsync(Staging, default);

        Assert.Empty(result.Promoted);
        Assert.Equal(["unsigned.pkg/1.0.0"], result.Skipped);
        Assert.False(Directory.Exists(Shared));
    }
}
