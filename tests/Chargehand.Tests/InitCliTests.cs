using System.Text.Json;
using Chargehand.Config;
using Chargehand.Onboarding;

namespace Chargehand.Tests;

/// <summary>chargehand init: the findings it prints and the starter profile it writes.</summary>
public class InitCliTests
{
    private static readonly string[] PresetAllow = ["api.anthropic.com", "registry.npmjs.org", "api.nuget.org", "pypi.org", "files.pythonhosted.org"];

    private static (int Code, string Output, string Error) Run(TempDir dir, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = InitCli.Run(args, dir.Path, Path.Combine(dir.Path, "default-profile.json"), PresetAllow, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void It_prints_what_it_found_and_writes_a_profile_that_loads_and_validates()
    {
        using var dir = new TempDir();
        dir.Write("repo/App.sln", "");
        dir.Write("repo/package.json", """{"scripts":{"test":"jest"}}""");
        dir.Write("repo/package-lock.json", "{}");
        var profilePath = Path.Combine(dir.Path, "out", "local.json");

        var (code, output, error) = Run(dir, "repo", "--profile", profilePath);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("found: dotnet (App.sln)", output);
        Assert.Contains("found: node (package.json, package-lock.json)", output);
        Assert.Contains("test command (context.verify), from App.sln: [\"dotnet\",\"test\",\"--nologo\"]", output);
        Assert.Contains("also found, from package-lock.json:", output);
        Assert.Contains("added to driven.network.allow: globalcdn.nuget.org", output);
        Assert.DoesNotContain("allow: api.nuget.org", output);
        Assert.Contains("still to fill in:", output);

        var text = File.ReadAllText(profilePath);
        using var doc = JsonDocument.Parse(text);
        Assert.True(ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
        var profile = Profile.Load(profilePath);
        Assert.Equal(["globalcdn.nuget.org"], profile.Driven!.Network!.Allow);
        Assert.False(profile.Driven.Enabled);
        Assert.Equal([Path.Combine(dir.Path, "repo")], profile.RepositoryRoots);
    }

    [Fact]
    public void Without_a_repository_argument_it_reads_the_working_directory_and_without_profile_it_writes_the_default_path()
    {
        using var dir = new TempDir();
        dir.Write("go.mod", "module x\n");

        var (code, output, _) = Run(dir);

        Assert.Equal(0, code);
        Assert.Contains("found: go (go.mod)", output);
        Assert.True(File.Exists(Path.Combine(dir.Path, "default-profile.json")));
    }

    [Fact]
    public void An_existing_profile_is_not_overwritten_without_force()
    {
        using var dir = new TempDir();
        dir.Write("go.mod", "module x\n");
        var existing = dir.Write("local.json", "mine");

        var (code, output, error) = Run(dir, ".", "--profile", existing);

        Assert.Equal(1, code);
        Assert.Equal("mine", File.ReadAllText(existing));
        Assert.Contains("--force", error);
        Assert.Contains("found: go", output);
        Assert.DoesNotContain("still to fill in", output);
    }

    [Fact]
    public void Force_overwrites_an_existing_profile()
    {
        using var dir = new TempDir();
        dir.Write("go.mod", "module x\n");
        var existing = dir.Write("local.json", "mine");

        var (code, output, _) = Run(dir, ".", "--profile", existing, "--force");

        Assert.Equal(0, code);
        Assert.Contains("overwrote", output);
        Assert.StartsWith("{", File.ReadAllText(existing));
    }

    [Fact]
    public void A_repository_with_no_marker_still_gets_a_profile_and_a_note_to_name_the_test_command()
    {
        using var dir = new TempDir();

        var (code, output, _) = Run(dir);

        Assert.Equal(0, code);
        Assert.Contains("found: no known ecosystem", output);
        Assert.Contains("test command (context.verify): none found", output);
        Assert.Contains("package hosts: none needed", output);
        Assert.Contains("no test command was found", output);
        Assert.Empty(Profile.Load(Path.Combine(dir.Path, "default-profile.json")).Driven!.Network!.Allow!);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--profile")]
    public void A_bad_option_is_a_usage_error(string option)
    {
        using var dir = new TempDir();

        var (code, _, error) = Run(dir, option);

        Assert.Equal(2, code);
        Assert.Contains("usage: chargehand init", error);
    }

    [Fact]
    public void A_path_that_is_not_a_directory_is_a_usage_error()
    {
        using var dir = new TempDir();

        var (code, _, error) = Run(dir, "missing");

        Assert.Equal(2, code);
        Assert.Contains("is not a directory", error);
        Assert.False(File.Exists(Path.Combine(dir.Path, "default-profile.json")));
    }

    [Fact]
    public void The_profile_names_secrets_by_item_and_never_holds_a_value()
    {
        using var dir = new TempDir();
        dir.Write("repo/go.mod", "module x\n");
        dir.Write("repo/.env", "API_KEY=hunter2-canary\n");
        dir.Write("repo/package.json", """{"name":"x"}""");

        var (_, output, _) = Run(dir, "repo");

        var text = File.ReadAllText(Path.Combine(dir.Path, "default-profile.json"));
        Assert.DoesNotContain("hunter2", text + output);
        using var doc = JsonDocument.Parse(text);
        var profile = Profile.Load(Path.Combine(dir.Path, "default-profile.json"));
        Assert.Equal(StarterProfile.ApiKeyItem, profile.Http!.ApiKeySecret);
        Assert.Equal(StarterProfile.PushItem, profile.Driven!.PushSecret);
        Assert.True(Assert.Single(profile.Secrets!).Env);
        foreach (var name in new[] { "api_key_secret", "push_secret" })
            Assert.All(Strings(doc.RootElement, name), v => Assert.Matches("^[a-z][a-z0-9-]*$", v));
    }

    private static IEnumerable<string> Strings(JsonElement element, string property) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().SelectMany(p => p.Name == property && p.Value.ValueKind == JsonValueKind.String ? [p.Value.GetString()!] : Strings(p.Value, property)),
            JsonValueKind.Array => element.EnumerateArray().SelectMany(e => Strings(e, property)),
            _ => [],
        };

    [Fact]
    public void Rendering_twice_gives_the_same_text()
    {
        Assert.Equal(StarterProfile.Render("/work/repo", ["a.example"]), StarterProfile.Render("/work/repo", ["a.example"]));
    }
}
