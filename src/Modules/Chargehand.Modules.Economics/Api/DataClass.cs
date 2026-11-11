namespace Chargehand.Modules.Economics.Api;

/// <summary>What a task's data may be shown to (ADR 0095). The router reads it before it picks a pool: only <see cref="Public"/> and <see cref="OwnerPrivate"/> may reach a model that trains on
/// its prompts. Zero is <see cref="Restricted"/>, so a value nobody set is the safe one.</summary>
public enum DataClass
{
    /// <summary>Not known to be harmless: employer data, other people's words (messages, mail, recordings, facts about people), secrets, or a repository nothing vouches for. Only models a pool lists as not training.</summary>
    Restricted = 0,

    /// <summary>A repository GitHub reports as public, or public web content: any model.</summary>
    Public,

    /// <summary>The owner's own private repository, one the profile names as holding no personal or employer data: any model.</summary>
    OwnerPrivate,
}

public static class DataClasses
{
    /// <summary>Whether data of this class may go to a model that trains on its prompts.</summary>
    public static bool MayTrain(this DataClass dataClass) => dataClass is DataClass.Public or DataClass.OwnerPrivate;
}

/// <summary>The class of the data a run on <paramref name="repositoryPath"/> touches. A classifier that cannot tell says <see cref="DataClass.Restricted"/>; it never throws on a lookup failure.</summary>
public interface IDataClassifier
{
    Task<DataClass> ClassifyAsync(string? repositoryPath, CancellationToken ct);
}

/// <summary>Everything is <see cref="DataClass.Restricted"/>: what a host without a classifier gets, so the router can only use models a pool lists as not training.</summary>
public sealed class RestrictedClassifier : IDataClassifier
{
    public static readonly RestrictedClassifier Instance = new();

    public Task<DataClass> ClassifyAsync(string? repositoryPath, CancellationToken ct) => Task.FromResult(DataClass.Restricted);
}
