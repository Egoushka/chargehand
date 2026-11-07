using System.Text;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>A hard stop can leave NUL bytes or half a record in the run log; a reader skips such a line and returns the rest.</summary>
public class RunLogDamageTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<string> _warnings = [];

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    private static StartRecord Start(string id) =>
        new(id, DateTimeOffset.UtcNow, new string('0', 32), new RunRequest("request/v1", "t", new RequestContext(false, "cheap")), Environment.ProcessId);

    private string Path() => System.IO.Path.Combine(_dir.Path, "log.jsonl");

    private (JsonlRunLog Log, string Path) Rig() => (new JsonlRunLog(Path(), _warnings.Add), Path());

    /// <summary>A log with run-before, the damage, then run-after.</summary>
    private async Task<JsonlRunLog> Damaged(byte[] damage)
    {
        var (log, path) = Rig();
        await log.AppendAsync(Start("run-before"), CancellationToken.None);
        await using (var file = new FileStream(path, FileMode.Append))
        {
            await file.WriteAsync(damage);
        }
        await log.AppendAsync(Start("run-after"), CancellationToken.None);
        return log;
    }

    private static async Task AssertEveryReaderSeesBothRuns(JsonlRunLog log)
    {
        var ct = CancellationToken.None;
        Assert.Equal(["run-before", "run-after"], (await log.ReadAllAsync(ct)).Starts.Select(s => s.RunId));
        Assert.Equal(["run-before", "run-after"], (await log.ReadRunsAsync(ct)).Starts.Select(s => s.RunId));
        Assert.Equal(["run-after", "run-before"], (await log.ListAsync(new RunListQuery(), ct)).Select(r => r.RunId).Order());
        Assert.NotNull((await log.ReadAsync("run-before", ct)).Start);
        Assert.NotNull((await log.ReadAsync("run-after", ct)).Start);
        Assert.Equal(new ActorSpend(0m, 0, 0m), await log.SpendAsync("nobody", DateTimeOffset.UtcNow.AddDays(-1), ct));
    }

    [Fact]
    public async Task A_line_of_NUL_bytes_is_skipped_with_one_warning_that_does_not_carry_the_bytes()
    {
        var log = await Damaged([.. new byte[4096], (byte)'\n']);

        await AssertEveryReaderSeesBothRuns(log);

        Assert.All(_warnings, w => Assert.Equal("chargehand: skipped unreadable run-log line 2 (4096 bytes)", w));
        Assert.NotEmpty(_warnings);
    }

    [Fact]
    public async Task A_half_written_record_is_skipped_and_the_lines_around_it_are_read()
    {
        var log = await Damaged(Encoding.UTF8.GetBytes("{\"type\":\"start\",\"run_id\":\"run-cut\",\"start\":\"2026-1\n"));

        await AssertEveryReaderSeesBothRuns(log);

        Assert.All(_warnings, w => Assert.StartsWith("chargehand: skipped unreadable run-log line 2 (", w));
        Assert.NotEmpty(_warnings);
    }

    [Fact]
    public async Task An_empty_line_is_skipped_without_a_warning()
    {
        var log = await Damaged(Encoding.UTF8.GetBytes("\n\n"));

        await AssertEveryReaderSeesBothRuns(log);

        Assert.Empty(_warnings);
    }

    [Fact]
    public async Task A_bad_line_is_reported_once_per_log_however_often_the_log_is_read()
    {
        var log = await Damaged([.. new byte[16], (byte)'\n']);

        await log.ReadAllAsync(CancellationToken.None);
        await log.ReadAllAsync(CancellationToken.None);
        await log.ReadRunsAsync(CancellationToken.None);

        Assert.Single(_warnings);
    }

    [Fact]
    public async Task Several_bad_lines_are_each_reported_with_their_own_line_number()
    {
        var (log, path) = Rig();
        await log.AppendAsync(Start("run-before"), CancellationToken.None);
        await File.AppendAllTextAsync(path, "{not json\n");
        await log.AppendAsync(Start("run-middle"), CancellationToken.None);
        await using (var file = new FileStream(path, FileMode.Append))
        {
            await file.WriteAsync(new byte[] { 0, 0, (byte)'\n' });
        }
        await log.AppendAsync(Start("run-after"), CancellationToken.None);

        var starts = (await log.ReadAllAsync(CancellationToken.None)).Starts;

        Assert.Equal(["run-before", "run-middle", "run-after"], starts.Select(s => s.RunId));
        Assert.Equal(["chargehand: skipped unreadable run-log line 2 (9 bytes)", "chargehand: skipped unreadable run-log line 4 (2 bytes)"], _warnings);
    }

    [Fact]
    public async Task A_damaged_log_is_left_as_it_was()
    {
        var log = await Damaged([.. new byte[64], (byte)'\n']);
        var before = await File.ReadAllBytesAsync(Path());

        await log.ReadAllAsync(CancellationToken.None);
        await log.AppendAsync(Start("run-later"), CancellationToken.None);

        var after = await File.ReadAllBytesAsync(Path());
        Assert.Equal(before, after[..before.Length]);
    }
}
