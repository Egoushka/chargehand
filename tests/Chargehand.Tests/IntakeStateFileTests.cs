using Chargehand.Automation;

namespace Chargehand.Tests;

/// <summary>A hard freeze or power loss left intake's state file empty and crash-looped the server: the file is written whole through a flushed temporary file, and a damaged one starts an empty state instead of failing the start.</summary>
public class IntakeStateFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 1, 6, 0, TimeSpan.Zero);

    [Fact]
    public void An_empty_file_starts_an_empty_state_and_warns()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "github.json");
        File.WriteAllText(path, "");
        var warnings = new List<string>();

        var state = IntakeState.Load(path, warnings.Add);

        Assert.Empty(state.Handled);
        Assert.Empty(state.Tasks);
        Assert.Contains("is empty", Assert.Single(warnings));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.corrupt-*"));
    }

    [Fact]
    public void A_truncated_file_starts_an_empty_state_and_is_kept_beside_it()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "github.json");
        const string truncated = "{\n  \"handled\": [\n    \"comment:octo/repo:5\",\n    \"comm";
        File.WriteAllText(path, truncated);
        var warnings = new List<string>();

        var state = IntakeState.Load(path, warnings.Add);

        Assert.Empty(state.Handled);
        var copy = Assert.Single(Directory.GetFiles(dir.Path, "github.json.corrupt-*"));
        Assert.Equal(truncated, File.ReadAllText(copy));
        Assert.Contains(Path.GetFileName(copy), Assert.Single(warnings));
    }

    [Fact]
    public void A_file_of_blanks_nul_bytes_or_null_starts_an_empty_state()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "github.json");
        foreach (var content in new[] { "null", "   \n", new string('\0', 64) })
        {
            File.WriteAllText(path, content);
            Assert.Empty(IntakeState.Load(path, _ => { }).Handled);
        }
    }

    [Fact]
    public void A_state_started_from_a_damaged_file_saves_over_it_and_loads_again()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "github.json");
        File.WriteAllText(path, "");

        var state = IntakeState.Load(path, _ => { });
        state.Handled.Add("comment:octo/repo:6");
        state.Save(Now);

        var warnings = new List<string>();
        Assert.Equal(["comment:octo/repo:6"], IntakeState.Load(path, warnings.Add).Handled);
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_good_file_loads_without_a_warning_or_a_copy()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "github.json");
        var state = IntakeState.Load(path);
        state.Handled.Add("comment:octo/repo:5");
        state.Save(Now);
        var warnings = new List<string>();

        Assert.Equal(["comment:octo/repo:5"], IntakeState.Load(path, warnings.Add).Handled);
        Assert.Empty(warnings);
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void A_save_that_cannot_write_its_temporary_file_leaves_the_earlier_file_whole()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "github.json");
        var state = IntakeState.Load(path);
        state.Handled.Add("comment:octo/repo:5");
        state.Save(Now);
        var before = File.ReadAllText(path);

        Directory.CreateDirectory(path + ".tmp"); // the temporary file's name is taken: the write fails before anything touches the state file
        state.Handled.Add("comment:octo/repo:6");
        Assert.ThrowsAny<Exception>(() => state.Save(Now));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(["comment:octo/repo:5"], IntakeState.Load(path, _ => { }).Handled);
    }

    [Fact]
    public void A_temporary_file_left_by_a_crash_is_replaced_by_the_next_save_and_never_read_as_state()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "github.json");
        File.WriteAllText(path + ".tmp", "{\"handled\": [\"half");
        var state = IntakeState.Load(path);
        Assert.Empty(state.Handled);

        state.Handled.Add("comment:octo/repo:7");
        state.Save(Now);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(["comment:octo/repo:7"], IntakeState.Load(path).Handled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    public void The_strict_read_reports_a_damaged_file_and_leaves_it_where_it_is(string content)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "github.json");
        File.WriteAllText(path, content);

        Assert.ThrowsAny<Exception>(() => IntakeState.Read(path));

        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }
}
