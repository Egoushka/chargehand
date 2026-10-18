using System.Net;
using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0068: a read-only run's result is posted to <c>notify_url</c> when it ends; the morning report is built from the server's data; a driven run posts nothing.</summary>
public class ScheduleResultTests
{
    private static readonly TimeSpan Hours4 = TimeSpan.FromHours(4);

    private static async Task<ScheduleRig> Started(ScheduleEntry entry)
    {
        var rig = new ScheduleRig(entry);
        await rig.Tick();
        await rig.Wake(Hours4);
        return rig;
    }

    [Fact]
    public async Task A_finished_read_only_run_is_posted_with_a_title_its_summary_and_findings_with_their_places()
    {
        using var rig = await Started(ScheduleRig.Docs());
        Assert.Empty(rig.Notifier.Posted);
        rig.Log.End("run-1", ScheduleRig.Result(summary: "One contradiction.", claims: [new Claim("README says --port, the code reads --listen", ["e1", "e2"], 0.9)],
            evidence: [new Evidence("e1", EvidenceKind.File, "README.md:12"), new Evidence("e2", EvidenceKind.File, "src/Cli.cs:40")]));

        await rig.Tick();

        var (url, title, body) = Assert.Single(rig.Notifier.Posted);
        Assert.Equal((ScheduleRig.Url, "chargehand: docs"), (url, title));
        Assert.Contains("One contradiction.", body);
        Assert.Contains("- README says --port, the code reads --listen (README.md:12; src/Cli.cs:40)", body);
        Assert.Empty(rig.State.Running);
    }

    [Fact]
    public async Task A_result_is_posted_once_and_a_failed_run_says_how_it_ended()
    {
        using var rig = await Started(ScheduleRig.Docs());
        rig.Log.End("run-1", ScheduleRig.Result(ResultStatus.Failed, summary: "") with { Error = new ResultError(ErrorCode.SessionFailed, "the session stopped", false) });

        await rig.Tick();
        await rig.Tick();

        var (_, _, body) = Assert.Single(rig.Notifier.Posted);
        Assert.StartsWith("Ended: failed. the session stopped", body);
    }

    [Fact]
    public async Task A_post_that_fails_is_tried_at_the_next_wake_and_the_run_stays_in_the_state_until_it_goes_through()
    {
        using var rig = await Started(ScheduleRig.Docs());
        rig.Log.End("run-1", ScheduleRig.Result());
        rig.Notifier.Fail = true;

        await rig.Tick();
        Assert.Empty(rig.Notifier.Posted);
        Assert.Equal(1, Assert.Single(rig.State.Running).Attempts);
        Assert.DoesNotContain(rig.Warnings, w => w.Contains("secret-topic"));

        rig.Notifier.Fail = false;
        await rig.Tick();

        Assert.Single(rig.Notifier.Posted);
        Assert.Empty(rig.State.Running);
    }

    [Fact]
    public async Task A_result_that_cannot_be_posted_is_given_up_after_the_attempts()
    {
        using var rig = await Started(ScheduleRig.Docs());
        rig.Log.End("run-1", ScheduleRig.Result());
        rig.Notifier.Fail = true;

        for (var i = 0; i < Scheduler.MaxPostAttempts; i++)
            await rig.Tick();

        Assert.Empty(rig.State.Running);
        Assert.Contains(rig.Warnings, w => w.Contains("gave up"));
    }

    [Fact]
    public async Task A_run_whose_server_is_gone_is_lost_after_a_while_and_says_so()
    {
        using var rig = await Started(ScheduleRig.Docs());
        rig.Log.Forget("run-1");

        await rig.Tick();
        Assert.Empty(rig.Notifier.Posted);
        await rig.Wake(Scheduler.LostAfter + TimeSpan.FromMinutes(1));

        Assert.Contains("was lost", Assert.Single(rig.Notifier.Posted).Body);
        Assert.Empty(rig.State.Running);
    }

    [Fact]
    public async Task A_driven_run_posts_nothing_when_it_ends_and_frees_the_entry()
    {
        using var rig = await Started(ScheduleRig.Deps());
        rig.Log.End("run-1", ScheduleRig.Result());

        await rig.Tick();

        Assert.Empty(rig.Notifier.Posted);
        Assert.Empty(rig.State.Running);
    }

    [Fact]
    public async Task Without_a_notify_url_a_read_only_result_stays_in_the_run_log_only()
    {
        using var rig = new ScheduleRig(new AutomationSchedules([ScheduleRig.Docs()]));
        await rig.Tick();
        await rig.Wake(Hours4);
        rig.Log.End("run-1", ScheduleRig.Result());

        await rig.Tick();

        Assert.Empty(rig.Notifier.Posted);
        Assert.Empty(rig.State.Running);
    }

    [Fact]
    public void A_long_result_is_cut_to_what_a_notification_carries()
    {
        var text = ScheduleResults.Format(ScheduleRig.Result(summary: new string('x', 10_000)));
        Assert.True(text.Length <= ScheduleResults.MaxChars + 10);
        Assert.EndsWith("[cut]", text);
    }

    [Fact]
    public async Task The_morning_report_is_built_from_the_servers_own_data_and_posted_without_starting_a_run()
    {
        using var rig = new ScheduleRig(ScheduleRig.Report());
        rig.Morning.Data = new MorningData(7, 4, 1, 1, 0, 1, 2.5m, 3, ["question: Add a retry", "failure: Fix the build"]);
        await rig.Tick();
        await rig.Wake(TimeSpan.FromHours(6));

        var (url, title, body) = Assert.Single(rig.Notifier.Posted);
        Assert.Equal(ScheduleRig.Url, url);
        Assert.Equal("chargehand morning report 2026-01-01", title);
        Assert.Equal("""
            Last 24 hours: 7 runs and batches started. 4 finished, 1 failed, 1 need you, 0 denied, 1 still going. Spend $2.50.

            Needs you: 3 open inbox item(s).
            - question: Add a retry
            - failure: Fix the build
            """.ReplaceLineEndings("\n"), body);
        Assert.Empty(rig.Starter.Started);
        Assert.Equal(rig.Time.GetUtcNow() - TimeSpan.FromHours(24), rig.Morning.Since);
        Assert.Equal(1, rig.State.StartsIn("morning", rig.Time.GetUtcNow()));
    }

    [Fact]
    public async Task A_morning_report_that_cannot_be_posted_is_tried_again_and_a_quiet_night_reads_plainly()
    {
        using var rig = new ScheduleRig(ScheduleRig.Report());
        await rig.Tick();
        rig.Notifier.Fail = true;
        await rig.Wake(TimeSpan.FromHours(6));
        Assert.Empty(rig.Notifier.Posted);
        Assert.Equal(0, rig.State.StartsIn("morning", rig.Time.GetUtcNow()));

        rig.Notifier.Fail = false;
        await rig.Wake(Scheduler.RetryAfter);

        Assert.StartsWith("Last 24 hours: 0 runs and batches started.", Assert.Single(rig.Notifier.Posted).Body);
        Assert.Contains("0 open inbox item(s).", rig.Notifier.Posted[0].Body);
    }

    [Fact]
    public void More_open_items_than_titles_are_counted_not_listed()
    {
        var text = MorningReport.Format(new MorningData(0, 0, 0, 0, 0, 0, 0m, 10, [.. Enumerable.Range(1, 8).Select(i => $"item {i}")]));
        Assert.Contains("(and 2 more)", text);
    }

    [Fact]
    public async Task The_http_notifier_posts_plain_text_with_a_title_header_and_never_names_the_url_in_a_failure()
    {
        var handler = new RecordingHandler();
        var notifier = new HttpScheduleNotifier(new HttpClient(handler));

        await notifier.PostAsync(ScheduleRig.Url, "chargehand: docs – ünï", "body text", default);

        Assert.Equal((HttpMethod.Post, ScheduleRig.Url, "body text", "chargehand: docs  n"), (handler.Method, handler.Url, handler.Body, handler.Title));
        Assert.StartsWith("text/plain", handler.ContentType);

        handler.Status = HttpStatusCode.ServiceUnavailable;
        var e = await Assert.ThrowsAsync<IntakeException>(() => notifier.PostAsync(ScheduleRig.Url, "t", "b", default));
        Assert.Equal(503, e.Status);
        Assert.DoesNotContain("secret-topic", e.Message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        public string? Title { get; private set; }
        public string? ContentType { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (Method, Url, Title, ContentType) = (request.Method, request.RequestUri!.ToString(), request.Headers.GetValues("Title").Single(), request.Content!.Headers.ContentType!.ToString());
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status);
        }
    }
}
