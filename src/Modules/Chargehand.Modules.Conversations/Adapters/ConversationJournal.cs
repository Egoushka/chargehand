using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Modules.Conversations.Api;

namespace Chargehand.Modules.Conversations.Adapters;

/// <summary>A conversation's events, one JSON line each, in <c>conversations/&lt;id&gt;.jsonl</c> beside the run log (ADR 0086). Append-only, as the run log is: the file is the
/// record, and the state of a conversation after a restart is its lines folded. A conversation holds what people typed and what the agent read, so the files are the owner's alone.</summary>
internal sealed partial class ConversationJournal(string directory)
{
    public static bool ValidId(string id) => IdPattern().IsMatch(id);

    public string PathOf(string id) =>
        ValidId(id) ? Path.Combine(directory, $"{id}.jsonl") : throw new ArgumentException($"'{id}' is not a conversation id", nameof(id));

    /// <summary>The ids that have a journal, sorted.</summary>
    public IReadOnlyList<string> Ids() =>
        !Directory.Exists(directory) ? []
        : [.. Directory.EnumerateFiles(directory, "conv-*.jsonl").Select(f => Path.GetFileNameWithoutExtension(f)).Where(ValidId).Order(StringComparer.Ordinal)];

    /// <summary>Every event of the conversation, oldest first. A line a crash cut short is skipped; none at all reads as empty.</summary>
    public IReadOnlyList<ConversationEvent> Read(string id)
    {
        var path = PathOf(id);
        if (!File.Exists(path))
            return [];
        var events = new List<ConversationEvent>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (JsonSerializer.Deserialize(line, ConversationJson.Event) is { } e)
                    events.Add(e);
            }
            catch (JsonException)
            {
                // Only the last line can be torn: the file is only ever appended to.
            }
        }
        return events;
    }

    /// <summary>The writer that appends to the conversation's file, which it creates with the directory when they are missing.</summary>
    public Writer Open(string id)
    {
        var path = PathOf(id);
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var torn = EndsMidLine(path);
        var writer = new StreamWriter(path, Encoding.UTF8, options) { AutoFlush = true, NewLine = "\n" };
        if (torn)
            writer.WriteLine();
        return new Writer(writer);
    }

    /// <summary>A file whose last line a crash cut short must not take the next event onto that line.</summary>
    private static bool EndsMidLine(string path)
    {
        if (!File.Exists(path))
            return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
            return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    [GeneratedRegex("^conv-[a-z0-9-]{1,64}$")]
    private static partial Regex IdPattern();

    public sealed class Writer(StreamWriter file) : IDisposable
    {
        public void Append(ConversationEvent e) => file.WriteLine(JsonSerializer.Serialize(e, ConversationJson.Event));

        public void Dispose() => file.Dispose();
    }
}
