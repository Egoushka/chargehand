using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Warming;

/// <summary>Runs <c>dotnet</c> outside the sandbox: the exit code and the combined output.</summary>
public delegate Task<(int ExitCode, string Output)> DotnetRunner(IReadOnlyList<string> args, string workDirectory, CancellationToken ct);

/// <summary>Fills the shared NuGet cache from a sandboxed restore's staging folder, outside the sandbox. NuGet checks a package's signature only when
/// it extracts it, and trusts what already sits in the cache, so the cache is never written by repository code. Only the <c>.nupkg</c> files in
/// <c>id/version/id.version.nupkg</c> are taken from staging (nothing else there is read, so a planted file goes nowhere); each is checked with
/// <c>dotnet nuget verify</c> against the trusted certificates, and the ones that pass are extracted by NuGet itself from a private feed with
/// <c>signatureValidationMode=require</c>. Unsigned, unverifiable and private-feed packages are skipped. Reasons go to stderr, never into the events.</summary>
public sealed partial class NuGetPromoter(string sharedPackages, string workRoot, IReadOnlyList<string> extraFingerprints, DotnetRunner dotnet) : IPackagePromoter
{
    /// <summary>The nuget.org repository signing certificates (SHA-256, read from signed packages with <c>dotnet nuget verify</c>). Another one is added with the profile's <c>nuget_trusted_signers</c>.</summary>
    public static IReadOnlyList<string> DefaultFingerprints { get; } =
    [
        "1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D",
        "5A2901D6ADA3D18260B9C6DFE2133C95D74B9EEF6AE0E5DC334C8454D1477DF4",
    ];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9]+(\\.[0-9]+){0,3}(-[A-Za-z0-9.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex FingerprintPattern();

    private readonly List<string> _extraFingerprints = Validated(extraFingerprints);

    /// <summary>A malformed signer is a configuration error, not something to drop quietly; the message names its position, never the value.</summary>
    private static List<string> Validated(IReadOnlyList<string> fingerprints)
    {
        for (var i = 0; i < fingerprints.Count; i++)
            if (!FingerprintPattern().IsMatch(fingerprints[i]))
                throw new ArgumentException($"nuget_trusted_signers entry {i + 1} is not a 64-character SHA-256 hex fingerprint", nameof(fingerprints));
        return [.. fingerprints.Select(f => f.ToUpperInvariant())];
    }

    public async Task<PromotionResult> PromoteAsync(string stagingDir, CancellationToken ct)
    {
        var work = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var candidates = Candidates(stagingDir);
            if (candidates.Count == 0)
                return new PromotionResult([], []);
            var feed = Path.Combine(work, "feed");
            Directory.CreateDirectory(feed);
            var skipped = new List<string>();
            var staged = new List<(string Name, string File)>();
            foreach (var (id, version, source) in candidates)
            {
                var name = $"{id}/{version}";
                var copy = Path.Combine(feed, $"{id}.{version}.nupkg");
                try
                {
                    File.Copy(source, copy);
                    staged.Add((name, copy));
                }
                catch (IOException)
                {
                    skipped.Add(name);
                }
            }
            var fingerprints = Fingerprints();
            var verified = await VerifyAsync(staged, fingerprints, skipped, work, ct).ConfigureAwait(false);
            if (verified.Count == 0)
                return new PromotionResult([], skipped);
            if (!await ExtractAsync(verified, fingerprints, feed, work, ct).ConfigureAwait(false))
                return new PromotionResult([], [.. skipped, .. verified.Select(v => v.Name)]);
            return new PromotionResult([.. verified.Select(v => v.Name)], skipped);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Console.Error.WriteLineAsync($"nuget promotion: could not remove its work folder: {e.Message}").ConfigureAwait(false);
            }
        }
    }

    private List<string> Fingerprints() => [.. DefaultFingerprints, .. _extraFingerprints];

    /// <summary>The regular <c>.nupkg</c> files at <c>staging/id/version/id.version.nupkg</c> with a plain id and version; links and everything else are ignored.</summary>
    private static List<(string Id, string Version, string File)> Candidates(string stagingDir)
    {
        var found = new List<(string, string, string)>();
        if (!Directory.Exists(stagingDir))
            return found;
        foreach (var idDir in Directory.EnumerateDirectories(stagingDir))
        {
            var id = Path.GetFileName(idDir);
            if (!IdPattern().IsMatch(id) || IsLink(idDir))
                continue;
            foreach (var versionDir in Directory.EnumerateDirectories(idDir))
            {
                var version = Path.GetFileName(versionDir);
                var file = Path.Combine(versionDir, $"{id}.{version}.nupkg");
                if (VersionPattern().IsMatch(version) && !IsLink(versionDir) && File.Exists(file) && !IsLink(file))
                    found.Add((id, version, file));
            }
        }
        return found;
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private async Task<List<(string Name, string File, string Id, string Version)>> VerifyAsync(List<(string Name, string File)> staged, List<string> fingerprints, List<string> skipped, string work, CancellationToken ct)
    {
        var verified = new List<(string Name, string File, string Id, string Version)>();
        if (staged.Count == 0)
            return verified;
        var batch = await dotnet([.. VerifyArgs(staged.Select(s => s.File), fingerprints)], work, ct).ConfigureAwait(false);
        var passed = batch.ExitCode == 0 ? staged : await PassingAsync(staged, fingerprints, work, ct).ConfigureAwait(false);
        foreach (var s in staged)
        {
            if (passed.Contains(s))
            {
                var parts = s.Name.Split('/');
                verified.Add((s.Name, s.File, parts[0], parts[1]));
            }
            else
            {
                File.Delete(s.File);
                skipped.Add(s.Name);
                await Console.Error.WriteLineAsync($"nuget promotion: {s.Name} skipped, not signed by a trusted signer").ConfigureAwait(false);
            }
        }
        return verified;
    }

    private async Task<List<(string Name, string File)>> PassingAsync(List<(string Name, string File)> staged, List<string> fingerprints, string work, CancellationToken ct)
    {
        var passed = new List<(string, string)>();
        foreach (var s in staged)
        {
            var one = await dotnet([.. VerifyArgs([s.File], fingerprints)], work, ct).ConfigureAwait(false);
            if (one.ExitCode == 0)
                passed.Add(s);
        }
        return passed;
    }

    private static IEnumerable<string> VerifyArgs(IEnumerable<string> files, List<string> fingerprints)
    {
        yield return "nuget";
        yield return "verify";
        foreach (var f in files)
            yield return f;
        foreach (var fp in fingerprints)
        {
            yield return "--certificate-fingerprint";
            yield return fp;
        }
    }

    /// <summary>Has NuGet extract the verified packages into the shared cache with a project that only names them (<c>PackageDownload</c>: no target-framework
    /// match, no dependencies) and the private feed as its only source, signatures required.</summary>
    private async Task<bool> ExtractAsync(List<(string Name, string File, string Id, string Version)> verified, List<string> fingerprints, string feed, string work, CancellationToken ct)
    {
        var project = Path.Combine(work, "project");
        Directory.CreateDirectory(project);
        const string empty = "<Project />";
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Build.props"), empty, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Build.targets"), empty, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(project, "Directory.Packages.props"), empty, ct).ConfigureAwait(false);
        var items = new StringBuilder();
        foreach (var v in verified)
            items.Append(CultureInfo.InvariantCulture, $"    <PackageDownload Include=\"{Escape(v.Id)}\" Version=\"[{Escape(v.Version)}]\" />\n");
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
                <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
              <ItemGroup>
            {items}  </ItemGroup>
            </Project>
            """;
        var projectFile = Path.Combine(project, "warm.csproj");
        await File.WriteAllTextAsync(projectFile, csproj, ct).ConfigureAwait(false);
        var config = Path.Combine(work, "nuget.config");
        await File.WriteAllTextAsync(config, Config(feed, fingerprints), ct).ConfigureAwait(false);
        var result = await dotnet(["restore", projectFile, "--packages", sharedPackages, "--configfile", config, "--no-cache"], project, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            await Console.Error.WriteLineAsync($"nuget promotion: extraction failed, nothing promoted: {result.Output}").ConfigureAwait(false);
        return result.ExitCode == 0;
    }


    private static string Escape(string s) => SecurityElement.Escape(s) ?? "";

    private static string Config(string feed, List<string> fingerprints)
    {
        var defaults = fingerprints.Take(DefaultFingerprints.Count);
        var extra = fingerprints.Skip(DefaultFingerprints.Count);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n");
        sb.Append(CultureInfo.InvariantCulture, $"    <add key=\"promote\" value=\"{Escape(feed)}\" />\n");
        sb.Append("  </packageSources>\n  <config>\n    <add key=\"signatureValidationMode\" value=\"require\" />\n  </config>\n  <trustedSigners>\n");
        sb.Append("    <repository name=\"nuget.org\" serviceIndex=\"https://api.nuget.org/v3/index.json\">\n");
        foreach (var fp in defaults)
            sb.Append(CultureInfo.InvariantCulture, $"      <certificate fingerprint=\"{fp}\" hashAlgorithm=\"SHA256\" allowUntrustedRoot=\"false\" />\n");
        sb.Append("    </repository>\n");
        var n = 0;
        foreach (var fp in extra)
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <author name=\"extra{++n}\">\n      <certificate fingerprint=\"{fp}\" hashAlgorithm=\"SHA256\" allowUntrustedRoot=\"false\" />\n    </author>\n");
        }
        sb.Append("  </trustedSigners>\n</configuration>\n");
        return sb.ToString();
    }
}
