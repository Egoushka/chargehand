using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace Chargehand.Tests;

public class SchemaCompatTests(ITestOutputHelper output)
{
    private static IReadOnlyList<string> Breaks(string before, string after)
    {
        using var b = JsonDocument.Parse(before);
        using var a = JsonDocument.Parse(after);
        return SchemaCompat.Breaks(b.RootElement, a.RootElement);
    }

    [Theory]
    [InlineData("removed property",
        """{ "properties": { "a": { "type": "string" }, "b": {} } }""",
        """{ "properties": { "a": { "type": "string" } } }""")]
    [InlineData("newly required",
        """{ "required": ["a"], "properties": { "a": {}, "b": {} } }""",
        """{ "required": ["a", "b"], "properties": { "a": {}, "b": {} } }""")]
    [InlineData("changed type",
        """{ "type": "string" }""",
        """{ "type": "integer" }""")]
    [InlineData("narrowed type set",
        """{ "type": ["number", "null"] }""",
        """{ "type": "number" }""")]
    [InlineData("type added where none was",
        """{ "description": "anything" }""",
        """{ "type": "object" }""")]
    [InlineData("removed enum value",
        """{ "enum": ["a", "b"] }""",
        """{ "enum": ["a"] }""")]
    [InlineData("changed const",
        """{ "const": "a" }""",
        """{ "const": "b" }""")]
    [InlineData("additionalProperties absent to false",
        """{ "properties": { "a": {} } }""",
        """{ "properties": { "a": {} }, "additionalProperties": false }""")]
    [InlineData("additionalProperties true to false",
        """{ "additionalProperties": true }""",
        """{ "additionalProperties": false }""")]
    [InlineData("changed $id",
        """{ "$id": "urn:x:v1" }""",
        """{ "$id": "urn:x:v2" }""")]
    [InlineData("new minLength",
        """{ "type": "string" }""",
        """{ "type": "string", "minLength": 1 }""")]
    [InlineData("raised minimum",
        """{ "minimum": 0 }""",
        """{ "minimum": 1 }""")]
    [InlineData("lowered maximum",
        """{ "maximum": 10 }""",
        """{ "maximum": 5 }""")]
    [InlineData("new maxLength",
        """{ "type": "string" }""",
        """{ "type": "string", "maxLength": 5 }""")]
    [InlineData("new pattern",
        """{ "type": "string" }""",
        """{ "type": "string", "pattern": "^a" }""")]
    [InlineData("nested property type in $defs",
        """{ "$defs": { "d": { "properties": { "x": { "type": "string" } } } } }""",
        """{ "$defs": { "d": { "properties": { "x": { "type": "number" } } } } }""")]
    [InlineData("removed $defs entry",
        """{ "$defs": { "d": {} } }""",
        """{ "$defs": {} }""")]
    [InlineData("removed enum value under items",
        """{ "items": { "enum": ["a", "b"] } }""",
        """{ "items": { "enum": ["b"] } }""")]
    [InlineData("tightened then branch",
        """{ "allOf": [ { "if": {}, "then": { "required": [] } } ] }""",
        """{ "allOf": [ { "if": {}, "then": { "required": ["a"] } } ] }""")]
    public void Breaking_change_is_reported(string rule, string before, string after) =>
        Assert.True(Breaks(before, after).Count > 0, $"{rule}: expected a breaking change");

    [Theory]
    [InlineData("new optional property",
        """{ "required": ["a"], "properties": { "a": {} } }""",
        """{ "required": ["a"], "properties": { "a": {}, "b": { "type": "string" } } }""")]
    [InlineData("new enum value",
        """{ "enum": ["a"] }""",
        """{ "enum": ["a", "b"] }""")]
    [InlineData("widened type",
        """{ "type": "number" }""",
        """{ "type": ["number", "null"] }""")]
    [InlineData("dropped required",
        """{ "required": ["a"], "properties": { "a": {} } }""",
        """{ "properties": { "a": {} } }""")]
    [InlineData("additionalProperties false to true",
        """{ "additionalProperties": false }""",
        """{ "additionalProperties": true }""")]
    [InlineData("looser constraints",
        """{ "minLength": 2, "maxLength": 5, "minimum": 1, "maximum": 3 }""",
        """{ "minLength": 1, "maxLength": 9, "maximum": 4 }""")]
    [InlineData("docs text",
        """{ "description": "old", "title": "T", "properties": { "a": { "description": "x" } } }""",
        """{ "description": "new", "title": "U", "properties": { "a": { "description": "y" } } }""")]
    [InlineData("new $defs entry",
        """{ "$defs": { "d": {} } }""",
        """{ "$defs": { "d": {}, "e": { "type": "string" } } }""")]
    public void Additive_change_passes(string rule, string before, string after)
    {
        var breaks = Breaks(before, after);
        Assert.True(breaks.Count == 0, $"{rule}: {string.Join("; ", breaks)}");
    }

    // A published major takes additive changes only: every schema file that shipped in the latest release tag
    // must still accept what it accepted then. New files and new vN directories are not in the tag, so they pass.
    [Fact]
    public void Published_schemas_change_only_additively_since_the_last_release()
    {
        var tag = Git("tag", "--list", "v*", "--sort=-v:refname").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (tag is null)
        {
            // CI must compare against a tag: the build job checks out with fetch-depth 0 so tags are present.
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "No v* release tag found in CI: fetch tags (checkout fetch-depth: 0).");
            output.WriteLine("SKIPPED: no v* release tag in this clone; run `git fetch --tags` to compare published schemas.");
            return;
        }

        var files = Git("ls-tree", "-r", "--name-only", tag, "--", "schemas").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.EndsWith(".schema.json", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var breaks = new List<string>();
        foreach (var file in files)
        {
            var current = Repo.Path(file.Split('/'));
            if (!File.Exists(current))
            {
                breaks.Add($"{file}: published schema removed");
                continue;
            }
            using var before = JsonDocument.Parse(Git("show", $"{tag}:{file}"));
            using var after = JsonDocument.Parse(File.ReadAllText(current));
            breaks.AddRange(SchemaCompat.Breaks(before.RootElement, after.RootElement).Select(b => $"{file}: {b}"));
        }
        output.WriteLine($"Compared {files.Count} schema files against {tag}.");
        Assert.True(breaks.Count == 0, $"Breaking changes to published schemas since {tag} (add a new vN directory instead):\n{string.Join("\n", breaks)}");
    }

    private static string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Result;
    }
}
