using System.Text.Json;
using Chargehand.Automation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary>A batch intake started for a source, newest first. <paramref name="Item"/> is what started it: <c>#12</c> (an issue), <c>#12 round 2</c> (a pull request's review round) or a Plane identifier (<c>ABC-12</c>).
/// <paramref name="Phase"/> is <c>running</c>, <c>asked</c> (waits for an answer on the issue) or <c>done</c>.</summary>
public sealed record IntakeSourceRunBody(string BatchRunId, string TaskId, string Item, DateTimeOffset Started, string Phase, string? PrUrl);

/// <param name="Origin"><c>listed</c> (the profile's <c>repositories</c>) or <c>discovered</c> (the App can see it; its checkout is a managed clone).</param>
/// <param name="Running">Batches intake started for the repository that are running.</param>
/// <param name="Queued">Triggers that wait: review comments for a round, and issues or answers told to wait for a free slot.</param>
/// <param name="Recent">Up to 5 batches intake started for it, newest first.</param>
public sealed record IntakeSourceBody(string Repository, bool Enabled, string Origin, string Path, int Running = 0, int Queued = 0, IReadOnlyList<IntakeSourceRunBody>? Recent = null);

/// <summary>A Plane project of <c>automation.plane.projects</c> and its switch (CHARGEHAND-242).</summary>
/// <param name="Remote">The repository as <c>owner/repo</c>, where the profile names it.</param>
public sealed record IntakePlaneSourceBody(string Project, bool Enabled, string Path, string? Remote, int Running, IReadOnlyList<IntakeSourceRunBody> Recent);

/// <param name="Configured">False when the profile has no <c>automation.plane</c>; the list is then empty.</param>
/// <param name="Queued">Work items told to wait for a free slot, over all projects: Plane intake's keys do not name the project.</param>
public sealed record IntakePlaneSourcesBody(bool Configured, int Queued, IReadOnlyList<IntakePlaneSourceBody> Projects);

/// <summary><c>intake-sources/v1</c>. <paramref name="Configured"/> is false when the profile has no <c>automation.github</c>; <see cref="Sources"/> is then empty. <paramref name="Plane"/> is Plane intake's projects (CHARGEHAND-242).</summary>
public sealed record IntakeSourcesBody(string ContractVersion, bool Configured, bool Discover, IReadOnlyList<IntakeSourceBody> Sources, IntakePlaneSourcesBody? Plane = null,
    IReadOnlyList<string>? NotRecorded = null);

/// <summary>The on/off switch of each source intake watches (CHARGEHAND-241, 242), for the console: <c>GET /v1/intake/sources</c> lists GitHub's repositories and Plane's projects with their queues, <c>PUT</c> with
/// <c>{"sources": [{"repository": "owner/repo", "enabled": false}], "plane_projects": [{"project": "&lt;id&gt;", "enabled": false}]}</c> (either list, or both) sets some and answers the list. The owner's alone.
/// A source intake does not watch is refused, so a typo cannot pass for a switch.</summary>
internal static class ConsoleSources
{
    public const string Contract = "intake-sources/v1";

    private const int MaxChanges = 200;
    private const int MaxRecent = 5;

    private static readonly IReadOnlyList<string> Unrecorded =
    [
        "Queued counts a trigger told to wait for a free slot until it starts: one withdrawn while it waited (its label removed, its item moved) is still counted.",
        "Plane intake's waiting items are counted for all projects together: its keys do not name the project.",
        "Running and recent batches are read from the state files the pollers last saved.",
    ];

    public static void Map(WebApplication app, ServerSettings settings, GitHubIntake? intake, PlaneIntake? plane)
    {
        app.MapGet("/v1/intake/sources", (HttpContext ctx) => ChargehandServer.OwnerOnly(ctx) ?? ChargehandServer.Json(Build(settings, intake, plane), StatusCodes.Status200OK));

        app.MapPut("/v1/intake/sources", async (HttpContext ctx) =>
        {
            if (ChargehandServer.OwnerOnly(ctx) is { } refused)
                return refused;
            if (await ChangesAsync(ctx) is not { } changes)
                return ChargehandServer.Json(ChargehandServer.Problem(
                    $"the body must be {{\"sources\": [{{\"repository\": \"owner/repo\", \"enabled\": true|false}}], \"plane_projects\": [{{\"project\": \"<id>\", \"enabled\": true|false}}]}} with 1 to {MaxChanges} entries in all"),
                    StatusCodes.Status400BadRequest);
            var (repositories, projects) = changes;
            if (repositories.Count > 0 && intake is null)
                return ChargehandServer.Json(ChargehandServer.Problem("GitHub intake is not configured (the profile has no automation.github)"), StatusCodes.Status409Conflict);
            if (projects.Count > 0 && plane is null)
                return ChargehandServer.Json(ChargehandServer.Problem("Plane intake is not configured (the profile has no automation.plane)"), StatusCodes.Status409Conflict);
            List<string> unknown = [.. repositories.Where(c => !intake!.Watched.ContainsKey(c.Name)).Select(c => c.Name), .. projects.Where(c => !plane!.Projects.ContainsKey(c.Name)).Select(c => c.Name)];
            if (unknown.Count > 0)
                return ChargehandServer.Json(ChargehandServer.Problem($"intake does not watch {string.Join(", ", unknown)}"), StatusCodes.Status404NotFound);
            try
            {
                foreach (var (repository, enabled) in repositories)
                    intake!.Sources.Set(repository, enabled);
                foreach (var (project, enabled) in projects)
                    plane!.Sources.Set(project, enabled);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ChargehandServer.Json(ChargehandServer.Problem($"the switches could not be written: {e.Message}"), StatusCodes.Status503ServiceUnavailable);
            }
            return ChargehandServer.Json(Build(settings, intake, plane), StatusCodes.Status200OK);
        });
    }

    public static IntakeSourcesBody Build(ServerSettings settings, GitHubIntake? intake, PlaneIntake? plane)
    {
        List<string> problems = [];
        var github = intake is null ? null : Read(settings.IntakeStatePath, IntakeState.Load, "GitHub", problems) ?? new IntakeState();
        var sources = intake is null ? [] : intake.Watched.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase)
            .Select(w => GitHubSource(w.Key, intake.Sources.IsOn(w.Key), w.Value, github!)).ToList();
        return new(Contract, intake is not null, intake?.Discovers ?? false, sources, PlaneSources(settings, plane, problems), [.. problems, .. Unrecorded]);
    }

    private static IntakeSourceBody GitHubSource(string repository, bool on, WatchedRepository watched, IntakeState state)
    {
        var tasks = state.Tasks.Where(t => Same(t.Repository, repository)).ToList();
        var waiting = state.Notified.Count(k => WaitsForSlot(k, repository) && !state.Handled.Contains(k["wait:".Length..]));
        return new(repository, on, watched.Discovered ? "discovered" : "listed", watched.Path, tasks.Count(t => t.Phase == IntakePhase.Running),
            state.Pending.Count(p => Same(p.Repository, repository)) + waiting,
            [.. tasks.OrderByDescending(t => t.Started).Take(MaxRecent).Select(t => new IntakeSourceRunBody(t.BatchRunId, t.TaskId,
                t.Round is { } round ? $"#{t.Issue} round {round}" : $"#{t.Issue}", t.Started, t.Phase.ToString().ToLowerInvariant(), t.PrUrl))]);
    }

    /// <summary>A wait notice of an issue's label or an answer: <c>wait:label:owner/repo#12:&lt;id&gt;</c>, <c>wait:comment:owner/repo:&lt;id&gt;</c>. A round's (<c>wait:round:</c>) is counted by its pending comments.</summary>
    private static bool WaitsForSlot(string key, string repository) =>
        key.StartsWith($"wait:label:{repository}#", StringComparison.OrdinalIgnoreCase) || key.StartsWith($"wait:comment:{repository}:", StringComparison.OrdinalIgnoreCase);

    private static IntakePlaneSourcesBody PlaneSources(ServerSettings settings, PlaneIntake? plane, List<string> problems)
    {
        if (plane is null)
            return new(false, 0, []);
        var state = Read(settings.PlaneStatePath, PlaneState.Load, "Plane", problems) ?? new PlaneState();
        var projects = plane.Projects.OrderBy(p => p.Value.Remote ?? p.Key, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            var tasks = state.Tasks.Where(t => t.Project == p.Key).ToList();
            return new IntakePlaneSourceBody(p.Key, plane.Sources.IsOn(p.Key), p.Value.Path, p.Value.Remote, tasks.Count(t => t.Phase == PlanePhase.Running),
                [.. tasks.OrderByDescending(t => t.Started).Take(MaxRecent).Select(t => new IntakeSourceRunBody(t.BatchRunId, t.TaskId, t.Identifier, t.Started, t.Phase.ToString().ToLowerInvariant(), null))]);
        }).ToList();
        var queued = state.Notified.Count(k => k.StartsWith("wait:", StringComparison.Ordinal) && !state.Handled.Contains(k["wait:".Length..]));
        return new(true, queued, projects);
    }

    private static T? Read<T>(string? path, Func<string, T> load, string which, List<string> problems) where T : class
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            return load(path);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
        {
            problems.Add($"{which} intake's state file could not be read, so its queues show 0: {e.Message}");
            return null;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static async Task<(List<(string Name, bool Enabled)> Repositories, List<(string Name, bool Enabled)> Projects)?> ChangesAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var repositories = Entries(doc.RootElement, "sources", "repository");
            var projects = Entries(doc.RootElement, "plane_projects", "project");
            if (repositories is null || projects is null || !doc.RootElement.TryGetProperty("sources", out _) && !doc.RootElement.TryGetProperty("plane_projects", out _))
                return null;
            return repositories.Count + projects.Count is > 0 and <= MaxChanges ? (repositories, projects) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The entries of one list, empty when it is left out; null when it is malformed.</summary>
    private static List<(string, bool)>? Entries(JsonElement root, string list, string name)
    {
        if (!root.TryGetProperty(list, out var entries))
            return [];
        if (entries.ValueKind != JsonValueKind.Array)
            return null;
        List<(string, bool)> changes = [];
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { Length: > 0 } id || !entry.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            changes.Add((id, enabled.GetBoolean()));
        }
        return changes;
    }
}
