using Chargehand.Modules.Conversations.Adapters;
using Chargehand.Modules.Conversations.Api;

namespace Chargehand.Modules.Conversations.Tests;

public sealed class ConversationJournalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("chargehand-conversations-").FullName;

    private string Directory_ => Path.Combine(_root, "conversations");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Events_are_read_back_in_the_order_they_were_appended()
    {
        var journal = new ConversationJournal(Directory_);
        var events = Events.Opened().Add(new MessageChunk("user", "hi")).List;
        using (var writer = journal.Open("conv-1"))
            foreach (var e in events)
                writer.Append(e);

        var read = journal.Read("conv-1");

        Assert.Equal(events.Select(e => (e.Id, e.Type)), read.Select(e => (e.Id, e.Type)));
        Assert.Equal("hi", ConversationJson.Parse(read[^1]) is MessageChunk chunk ? chunk.Text : null);
        Assert.Equal(["conv-1"], journal.Ids());
    }

    [Fact]
    public void A_writer_that_is_reopened_adds_to_the_same_file()
    {
        var journal = new ConversationJournal(Directory_);
        var events = Events.Opened().Add(new EndedData("lost")).List;
        using (var first = journal.Open("conv-1"))
            first.Append(events[0]);
        using (var second = journal.Open("conv-1"))
            foreach (var e in events.Skip(1))
                second.Append(e);

        Assert.Equal([1L, 2, 3], journal.Read("conv-1").Select(e => e.Id));
    }

    [Fact]
    public void A_line_torn_by_a_crash_is_skipped_and_the_events_before_it_are_kept()
    {
        var journal = new ConversationJournal(Directory_);
        using (var writer = journal.Open("conv-1"))
            foreach (var e in Events.Opened().List)
                writer.Append(e);
        File.AppendAllText(journal.PathOf("conv-1"), """{"contract_version":"conversation-event/v1","id":3,"at":"2026-10-28T09:00""");

        Assert.Equal([1L, 2], journal.Read("conv-1").Select(e => e.Id));
    }

    [Theory]
    [InlineData("../run-log")]
    [InlineData("conv-1/../../x")]
    [InlineData("")]
    [InlineData("run-20261028-090000-abcdef")]
    [InlineData("CONV-1")]
    public void An_id_that_is_not_ours_never_reaches_the_file_system(string id)
    {
        var journal = new ConversationJournal(Directory_);

        Assert.False(ConversationJournal.ValidId(id));
        Assert.Throws<ArgumentException>(() => journal.PathOf(id));
    }

    [Fact]
    public void A_conversation_with_no_file_reads_as_empty_and_a_missing_directory_lists_nothing()
    {
        var journal = new ConversationJournal(Directory_);

        Assert.Empty(journal.Read("conv-9"));
        Assert.Empty(journal.Ids());
    }

    [Fact]
    public void The_files_are_for_their_owner_alone()
    {
        if (OperatingSystem.IsWindows())
            return;
        var journal = new ConversationJournal(Directory_);
        using (var writer = journal.Open("conv-1"))
            writer.Append(Events.Opened().List[0]);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(journal.PathOf("conv-1")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Directory_));
    }
}
