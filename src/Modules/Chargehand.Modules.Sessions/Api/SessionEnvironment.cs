using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>What the server knows about the task's repository and the batch, for the session's environment note (ADR 0048). The toolchains of the image are not here: the session reads
/// them from the image itself.</summary>
/// <param name="Ecosystems">The names <c>RepositoryDetector</c> found at the repository's top level, such as <c>dotnet</c>.</param>
/// <param name="Verify">The command chargehand runs on the branch afterwards; null: none could be found.</param>
/// <param name="VerifySource"><c>request</c> (the caller named it), <c>ci</c> (read from the repository's CI workflow) or <c>detected</c>.</param>
/// <param name="Caches">Ecosystems whose cache is a shared volume mounted in this container (see <see cref="CacheVolumes"/>).</param>
/// <param name="Services">Test services of the batch, reachable by name.</param>
public sealed record SessionEnvironment(IReadOnlyList<string> Ecosystems, IReadOnlyList<string>? Verify = null, string? VerifySource = null, IReadOnlyList<string>? Caches = null,
    IReadOnlyList<EnvironmentService>? Services = null);

/// <param name="Address"><c>name:port</c> on the batch network.</param>
/// <param name="Note">The profile's one line on how to connect.</param>
public sealed record EnvironmentService(string Name, string Address, string? Note = null);

/// <summary>The tools a session image has, measured from their own <c>--version</c> output, and the ones it lacks (ADR 0048). The image build writes it (<c>chargehand session --probe</c>)
/// next to the driver, so a session reads it instead of running <c>which</c> and <c>--version</c> turn after turn; an image without the file is probed once by the driver.</summary>
/// <param name="Tools">Name to the first line of the tool's version output.</param>
/// <param name="Absent">Probed and not found.</param>
public sealed partial record ToolchainManifest(IReadOnlyDictionary<string, string> Tools, IReadOnlyList<string> Absent)
{
    public const string DefaultPath = "/opt/chargehand/environment.json";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What is probed and how. The same table is the manifest's vocabulary, so a note never names a tool nobody looked for.</summary>
    public static readonly IReadOnlyList<(string Name, string[] Args)> Probes =
    [
        ("dotnet", ["--version"]), ("node", ["--version"]), ("npm", ["--version"]), ("python3", ["--version"]), ("pip3", ["--version"]), ("git", ["--version"]),
        ("java", ["-version"]), ("gradle", ["--version"]), ("mvn", ["--version"]), ("go", ["version"]), ("cargo", ["--version"]), ("docker", ["--version"]),
    ];

    private static readonly SessionsJsonContext Json = new(new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });

    /// <returns>Null when the file is missing or not a manifest.</returns>
    public static ToolchainManifest? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), Json.ToolchainManifest) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json.ToolchainManifest);

    /// <param name="run">Runs a tool with its arguments and returns its combined output; null when the tool is not there or did not answer.</param>
    public static async Task<ToolchainManifest> ProbeAsync(Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>> run, CancellationToken ct)
    {
        SortedDictionary<string, string> tools = new(StringComparer.Ordinal);
        List<string> absent = [];
        foreach (var (name, args) in Probes)
            if (VersionLine(await run(name, args, ct)) is { } version)
                tools[name] = version;
            else
                absent.Add(name);
        return new ToolchainManifest(tools, absent);
    }

    public static Task<ToolchainManifest> ProbeAsync(CancellationToken ct) => ProbeAsync(RunToolAsync, ct);

    /// <summary>The version in the first line of the output that has a digit (Gradle prints a rule of dashes first): <c>v26.10.0</c>, <c>3.12.3</c>; the line itself, cut at 80 characters, when it has no dotted number. Null when no line has a digit.</summary>
    internal static string? VersionLine(string? output)
    {
        if (output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Any(char.IsDigit)) is not { } line)
            return null;
        return Version().Match(line) is { Success: true } m ? m.Value : line.Length > 80 ? line[..80] : line;
    }

    [GeneratedRegex(@"v?\d+(\.\d+)+")]
    private static partial Regex Version();

    private static async Task<string?> RunToolAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + file);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? await output + "\n" + await error : null;
        }
    }
}

/// <summary>The environment note (ADR 0048): a few lines, appended to the session's prompt next to the static text, that say what the container has, what it lacks and how the tests run, so
/// the model does not spend turns finding out. It is built in the trusted driver from the image's manifest and the server's facts, and kept out of the goal so a research call does not
/// carry it.</summary>
public static class EnvironmentNote
{
    public static string Render(ToolchainManifest toolchains, SessionEnvironment? environment)
    {
        var note = new StringBuilder("Environment (measured by chargehand; read it here instead of probing with `which`, `--version` or `ls /usr/bin`):\n");
        note.Append("- Installed: ").AppendJoin(", ", toolchains.Tools.Select(t => $"{t.Key} {t.Value}")).Append(".\n");
        if (toolchains.Absent.Count > 0)
            note.Append("- Not installed: ").AppendJoin(", ", toolchains.Absent).Append(". Do not look for them or try to install them; if the task needs one, say so in the report.\n");
        note.Append("- Network: only package registries on an allowlist answer, through the proxy in HTTPS_PROXY; every other host fails. There is no Docker, and you are not root.\n");
        if (environment is null)
            return note.ToString();
        if (environment.Ecosystems.Count > 0)
            note.Append("- This repository uses: ").AppendJoin(", ", environment.Ecosystems).Append(".\n");
        if (environment.Verify is { Count: > 0 } verify)
            note.Append(CultureInfo.InvariantCulture, $"- After you finish, chargehand runs `{string.Join(' ', verify)}` on your branch in a fresh container ({VerifySourceNote(environment.VerifySource)}). Run the same command yourself.\n");
        else
            note.Append("- chargehand found no test command for this repository, so it will not push the branch unless the tests run; find the command in the repository's own files.\n");
        if (environment.Caches is { Count: > 0 } caches)
            note.Append("- Package caches are warm and shared by this repository's tasks: ")
                .AppendJoin(", ", caches.Where(CacheVolumes.Ecosystems.ContainsKey).Select(c => $"{c} in {CacheVolumes.MountRoot}/{c}"))
                .Append(". They are already configured through the package managers' environment variables; leave them set.\n");
        else
            note.Append("- Package caches are empty and belong to this task alone (NuGet, Gradle and Maven in .nuget, .gradle and .m2 under /work, the others under /home/session); the first restore downloads everything.\n");
        if (environment.Services is { Count: > 0 } services)
        {
            note.Append("- Test services on the network, started for this batch:\n");
            foreach (var service in services)
                note.Append(CultureInfo.InvariantCulture, $"  - {service.Name} at {service.Address}{(service.Note is { Length: > 0 } text ? $": {text}" : "")}\n");
            note.Append("  A service may need a few seconds after the container starts; retry a refused connection before concluding it is down.\n");
        }
        return note.ToString();
    }

    private static string VerifySourceNote(string? source) => source switch
    {
        "request" => "the command the task named",
        "ci" => "read from this repository's CI workflow",
        _ => "detected from the repository",
    };
}
