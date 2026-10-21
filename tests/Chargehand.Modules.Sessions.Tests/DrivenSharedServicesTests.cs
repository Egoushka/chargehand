using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>ADR 0060: <c>driven.services</c> is bound by the Sessions module's source-generated context and refused by its generated validator, with the message the
/// profile loader in <c>Chargehand.Config</c> wrote before the move, word for word.</summary>
public class DrivenSharedServicesTests
{
    private static readonly string Pinned = "registry.example/jdk@sha256:" + new string('3', 64);
    private static readonly string Postgres = "docker.io/library/postgres@sha256:" + new string('4', 64);

    [Fact]
    public void Every_problem_is_named_in_order_under_the_section_with_the_hint()
    {
        var services = new DrivenSharedServices(["ruby", "npm", "npm"],
            new Dictionary<string, string> { ["Gradle"] = "registry.example/jdk:latest", ["maven"] = Pinned, ["node"] = Postgres },
            new Dictionary<string, IReadOnlyList<DrivenTestService>>
            {
                ["not a repo"] = [],
                ["o/r"] =
                [
                    new("Postgres", "postgres:16", 0, new Dictionary<string, string> { ["LD_PRELOAD"] = "x", ["OK"] = "a\nb" }, ["../etc", "/ok"], "line\nbreak", 10),
                    new("db", Postgres, 5432),
                    new("db", Postgres, 5432),
                    new("c", Postgres, 1),
                    new("d", Postgres, 1),
                ],
            });

        var e = Assert.Throws<ChargehandException>(() => services.Validate([Postgres]));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Equal("driven.services: caches entry 'ruby' must be one of nuget, npm, pip, gradle, maven; caches lists an ecosystem twice; "
            + "images key 'Gradle' must be an ecosystem name such as gradle; images.Gradle must be pinned by digest (name@sha256:<64 hex>); "
            + "images.maven must also be listed in driven.images, which is what the runner allows; repositories key 'not a repo' must be owner/repo; "
            + "repositories.o/r has more than 4 services; repositories.o/r names a service twice; "
            + "repositories.o/r.Postgres: name must be lowercase letters and digits, starting with a letter, at most 24; "
            + "repositories.o/r.Postgres: image must be pinned by digest (name@sha256:<64 hex>); repositories.o/r.Postgres: port must be 1 to 65535; "
            + "repositories.o/r.Postgres: memory_mb must be 64 to 8192; "
            + "repositories.o/r.Postgres: env name 'LD_PRELOAD' must be upper case letters, digits and underscores, and not LD_*; "
            + "repositories.o/r.Postgres: env value of 'OK' has a line break or NUL; "
            + "repositories.o/r.Postgres: tmpfs entry '../etc' must be an absolute path of plain segments; "
            + "repositories.o/r.Postgres: note must be one line of at most 200 characters", e.Message);
        Assert.Equal("Fix driven.services in the profile; docs/guide/driven.md lists the keys.", e.Action);
    }

    [Fact]
    public void Settings_that_can_work_pass()
    {
        var services = new DrivenSharedServices(["nuget"], new Dictionary<string, string> { ["gradle"] = Pinned },
            new Dictionary<string, IReadOnlyList<DrivenTestService>> { ["o/r"] = [new("postgres", Postgres, 5432)] });
        services.Validate([Pinned]);
    }

    [Fact]
    public void The_section_reads_back_as_reflection_reads_it()
    {
        const string json = """
            {"caches":["nuget"],"images":{"gradle":"jdk"},"unknown":1,
             // a comment
             "repositories":{"o/r":[{"name":"postgres","image":"pg","port":5432,"env":{"A":"b"},"tmpfs":["/d"],"note":"n","memory_mb":256}]},
             "verify":{"o/r":["dotnet","test","--filter","Category!=Docker"]}}
            """;
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            ReadCommentHandling = JsonCommentHandling.Skip,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var reflection = JsonSerializer.Deserialize<DrivenSharedServices>(json, options);
        var generated = JsonSerializer.Deserialize(json, SessionsSettingsJsonContext.Default.DrivenSharedServices);
        Assert.Equal(JsonSerializer.Serialize(reflection, options), JsonSerializer.Serialize(generated, options));
        Assert.Equal(["dotnet", "test", "--filter", "Category!=Docker"], generated!.VerifyFor(null, "o/r"));
    }

    [Fact]
    public void A_request_without_verify_takes_the_repositorys_command_from_the_profile()
    {
        // Testcontainers suites fail in the verifier, which has no engine; the owner names a command that skips them, per repository.
        var services = new DrivenSharedServices(Verify: new Dictionary<string, IReadOnlyList<string>> { ["Owner/Repo"] = ["dotnet", "test", "--filter", "Category!=Docker"] });

        Assert.Equal(["dotnet", "test", "--filter", "Category!=Docker"], services.VerifyFor(null, "owner/repo"));
        Assert.Equal(["make", "check"], services.VerifyFor(["make", "check"], "owner/repo"));
        Assert.Null(services.VerifyFor(null, "other/repo"));
        Assert.Null(services.VerifyFor([], "other/repo"));
    }

    [Fact]
    public void A_verify_entry_needs_an_owner_repo_key_and_a_command()
    {
        var services = new DrivenSharedServices(Verify: new Dictionary<string, IReadOnlyList<string>> { ["not a repo"] = ["x"], ["o/r"] = [] });

        var e = Assert.Throws<ChargehandException>(() => services.Validate([]));

        Assert.Contains("verify key 'not a repo' must be owner/repo", e.Message, StringComparison.Ordinal);
        Assert.Contains("verify.o/r must name a command", e.Message, StringComparison.Ordinal);
    }
}
