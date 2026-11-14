using System.Text.Json;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>A rule chargehand proposes about its own pull requests (issue #452), after a human fix of the same <see cref="HumanEditCategory"/> recurred: never pushed by itself, only
/// written for a person to turn into a repository rule line, a pre-push check, a tracker item or a draft pull request. <paramref name="Examples"/> are the pull requests the proposal
/// is built from.</summary>
public sealed record ProposalRecord(DateTimeOffset At, string Category, int Occurrences, string Title, string Body, IReadOnlyList<string> Examples);

/// <summary>Where a <see cref="ProposalRecord"/> goes once <see cref="HumanEditLearner"/> writes one. Writing here is the whole of the action: nothing here pushes a commit, opens a pull
/// request or calls a tracker on its own.</summary>
public interface IProposalSink
{
    Task WriteAsync(ProposalRecord proposal, CancellationToken ct);

    Task<IReadOnlyList<ProposalRecord>> ReadAsync(CancellationToken ct);
}

/// <summary>One JSON object per line, append-only, written without reflection (ADR 0060).</summary>
public sealed class JsonlProposalSink(string path) : IProposalSink
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task WriteAsync(ProposalRecord proposal, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var line = JsonSerializer.Serialize(proposal, HumanEditJsonContext.Default.ProposalRecord) + "\n";
            await File.AppendAllTextAsync(path, line, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProposalRecord>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var text = await File.ReadAllTextAsync(path, ct);
        List<ProposalRecord> proposals = [];
        foreach (var line in text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (JsonSerializer.Deserialize(line, HumanEditJsonContext.Default.ProposalRecord) is { } proposal)
                    proposals.Add(proposal);
            }
            catch (JsonException)
            {
            }
        }
        return proposals;
    }
}
