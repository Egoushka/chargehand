using Chargehand.Containers;

namespace Chargehand.Tests;

/// <summary>ADR 0039: the helper that fills a session's workspace volume from the read-only source, and what it can never hold.</summary>
public class WorkspaceTemplateTests
{
    private const string Image = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static WorkspaceSpec Spec() => new("run1", Image, "/srv/chargehand/checkouts/repo-abc1234", "chargehand-work-run1", "chargehand/run1", "abc1234");

    [Fact]
    public void The_helper_has_no_network_a_read_only_source_and_one_writable_volume()
    {
        var args = ContainerTemplate.WorkspaceArgs(Spec());
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal("none", After("--network"));
        Assert.Contains("--read-only", args);
        Assert.Equal("ALL", After("--cap-drop"));
        Assert.Equal("no-new-privileges", After("--security-opt"));
        Assert.Equal("10001:10001", After("--user"));
        Assert.Equal("sh", After("--entrypoint"));
        Assert.Equal(["-v", "/srv/chargehand/checkouts/repo-abc1234:/src:ro", "-v", "chargehand-work-run1:/work"],
            args.Where((a, i) => a == "-v" || (i > 0 && args[i - 1] == "-v")));
        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain("--env-file", args);
        Assert.DoesNotContain("-e", args);
    }

    [Fact]
    public void The_script_is_fixed_and_the_branch_and_commit_are_only_arguments_to_it()
    {
        var args = ContainerTemplate.WorkspaceArgs(Spec());
        var image = args.ToList().IndexOf(Image);
        Assert.Equal("-c", args[image + 1]);
        Assert.Contains("clone --quiet", args[image + 2]);
        Assert.Contains("--no-hardlinks", args[image + 2]);
        Assert.Contains("core.hooksPath", args[image + 2]);
        Assert.DoesNotContain("chargehand/run1", args[image + 2]);          // never spliced into the script text
        Assert.DoesNotContain("abc1234", args[image + 2]);
        Assert.Equal(["prepare", "chargehand/run1", "abc1234"], args.Skip(image + 3));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/srv/x:/etc:rw")]                 // a colon adds a mount option
    [InlineData("/srv/x,y")]
    [InlineData("/srv/x y")]
    [InlineData("")]
    [InlineData("/")]
    public void The_source_must_be_a_plain_absolute_path(string path) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.WorkspaceArgs(Spec() with { SourcePath = path }));

    [Theory]
    [InlineData("main")]
    [InlineData("chargehand/")]
    [InlineData("chargehand/--force")]
    [InlineData("chargehand/a b")]
    [InlineData("chargehand/../x")]
    public void The_branch_must_be_under_chargehand(string branch) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.WorkspaceArgs(Spec() with { Branch = branch }));

    [Theory]
    [InlineData("--upload-pack=x")]
    [InlineData("HEAD")]
    [InlineData("abc")]
    [InlineData("zzzzzzz")]
    public void The_commit_must_be_a_hex_hash(string commit) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.WorkspaceArgs(Spec() with { Commit = commit }));

    [Fact]
    public void Volume_and_run_id_follow_the_same_rules_as_a_session()
    {
        Assert.Throws<ArgumentException>(() => ContainerTemplate.WorkspaceArgs(Spec() with { WorkVolume = "x:/host" }));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.WorkspaceArgs(Spec() with { RunId = "--rm" }));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.WorkspaceArgs(Spec() with { Image = "img --cap-add=ALL" }));
    }
}
