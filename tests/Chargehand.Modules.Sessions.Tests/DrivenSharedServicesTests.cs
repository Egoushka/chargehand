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
            new Dictionary<string, IReadOnlyList<DrivenTestService>>
            {
                ["o/r"] = [new("postgres", Postgres, 5432, SessionEnv: new Dictionary<string, string> { ["SWITCHKNIFE_POSTGRES"] = "Host=postgres;Port=5432" })],
            });
        services.Validate([Pinned]);
    }

    [Fact]
    public void Session_env_problems_are_named_in_order()
    {
        var services = new DrivenSharedServices(Repositories: new Dictionary<string, IReadOnlyList<DrivenTestService>>
        {
            ["o/r"] = [new("postgres", Postgres, 5432, SessionEnv: new Dictionary<string, string>
            {
                ["bad_name"] = "x",
                ["PATH"] = "y",
                ["CHARGEHAND_FOO"] = "z",
                ["GOOD_NAME"] = "a\nb",
                ["ALSO_GOOD"] = "{secret:item}",
            })],
        });

        var e = Assert.Throws<ChargehandException>(() => services.Validate(null));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Equal("driven.services: repositories.o/r.postgres: session_env name 'bad_name' must be upper case letters, digits and underscores, starting with a letter; "
            + "repositories.o/r.postgres: session_env name 'PATH' is one chargehand already sets or reserves; "
            + "repositories.o/r.postgres: session_env name 'CHARGEHAND_FOO' is one chargehand already sets or reserves; "
            + "repositories.o/r.postgres: session_env value of 'GOOD_NAME' has a line break or NUL; "
            + "repositories.o/r.postgres: session_env value of 'ALSO_GOOD' must not reference the secret store ({secret:...}): it is set as written, and must not be a real secret", e.Message);
    }

    [Fact]
    public void Two_services_may_not_set_the_same_session_env_name()
    {
        var services = new DrivenSharedServices(Repositories: new Dictionary<string, IReadOnlyList<DrivenTestService>>
        {
            ["o/r"] =
            [
                new("postgres", Postgres, 5432, SessionEnv: new Dictionary<string, string> { ["DUP"] = "a" }),
                new("redis", Postgres, 6379, SessionEnv: new Dictionary<string, string> { ["DUP"] = "b" }),
            ],
        });

        var e = Assert.Throws<ChargehandException>(() => services.Validate(null));

        Assert.Equal("driven.services: repositories.o/r: session_env name 'DUP' is set by more than one service", e.Message);
    }

    [Fact]
    public void The_section_reads_back_as_reflection_reads_it()
    {
        const string json = """
            {"caches":["nuget"],"images":{"gradle":"jdk"},"unknown":1,
             // a comment
             "repositories":{"o/r":[{"name":"postgres","image":"pg","port":5432,"env":{"A":"b"},"tmpfs":["/d"],"note":"n","memory_mb":256,"session_env":{"S":"v"}}]}}
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
    }
}
