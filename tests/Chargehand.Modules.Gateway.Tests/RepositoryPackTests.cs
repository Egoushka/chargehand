using Chargehand.Modules.Gateway.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>Issue #435: the brief research and review steps load first instead of rediscovering a repository's map and conventions.</summary>
public class RepositoryPackTests
{
    [Fact]
    public void Renders_the_map_and_test_command_from_the_detector()
    {
        using var dir = new TempDir();
        dir.Write("go.mod", "module x\n");

        var text = RepositoryPack.Build(dir.Path).Render();

        Assert.Contains("Repository context pack", text, StringComparison.Ordinal);
        Assert.Contains("uses go", text, StringComparison.Ordinal);
        Assert.Contains("go test ./...", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_with_no_ecosystem_markers_says_so_instead_of_a_test_command()
    {
        using var dir = new TempDir();
        dir.Write("README.md", "hi\n");

        var text = RepositoryPack.Build(dir.Path).Render();

        Assert.Contains("no ecosystem markers", text, StringComparison.Ordinal);
        Assert.Contains("none detected", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_CLAUDE_md_conventions_when_there_is_no_AGENTS_md()
    {
        using var dir = new TempDir();
        dir.Write("CLAUDE.md", "Never commit secrets.\n");

        var text = RepositoryPack.Build(dir.Path).Render();

        Assert.Contains("Never commit secrets.", text, StringComparison.Ordinal);
        Assert.Contains("CLAUDE.md", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AGENTS_md_is_read_when_there_is_no_CLAUDE_md()
    {
        using var dir = new TempDir();
        dir.Write("AGENTS.md", "Run tests before a PR.\n");

        var text = RepositoryPack.Build(dir.Path).Render();

        Assert.Contains("Run tests before a PR.", text, StringComparison.Ordinal);
        Assert.Contains("AGENTS.md", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_with_neither_file_has_no_conventions_section()
    {
        using var dir = new TempDir();
        dir.Write("README.md", "hi\n");

        var text = RepositoryPack.Build(dir.Path).Render();

        Assert.DoesNotContain("Conventions", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_conventions_file_is_truncated_so_the_pack_stays_cheap()
    {
        using var dir = new TempDir();
        dir.Write("CLAUDE.md", new string('x', 9000));

        var text = RepositoryPack.Build(dir.Path).Render();

        Assert.Contains("[... truncated]", text, StringComparison.Ordinal);
        Assert.True(text.Length < 8500, $"expected a bounded pack, got {text.Length} characters");
    }
}
