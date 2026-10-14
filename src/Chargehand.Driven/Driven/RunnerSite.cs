using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Driven;

/// <summary>One runner as the server holds it (ADR 0058): its settings from the profile and the clients that reach its engine. A profile with <c>driven.runner</c> has one, named
/// <see cref="DrivenSettings.DefaultRunnerName"/>.</summary>
/// <param name="Config">What the profile says about it.</param>
/// <param name="Engine">The engine that runs its containers, volumes and networks.</param>
/// <param name="Workspace">Fills a workspace volume on that engine: from a directory both machines see, or from a bundle the server sends.</param>
/// <param name="Volumes">Moves a session's task and results through an output volume on that engine.</param>
/// <param name="Host">Its <c>GET /host</c>; null: tasks are placed on it by count only.</param>
public sealed record RunnerSite(DrivenRunnerEntry Config, IContainerEngine Engine, IWorkspaceEngine Workspace, ISessionVolumes Volumes, IHostSource? Host = null)
{
    public string Name => Config.Name;
}
