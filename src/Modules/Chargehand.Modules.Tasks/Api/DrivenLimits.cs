using System.ComponentModel.DataAnnotations;
using Chargehand.Kernel;
using Microsoft.Extensions.Options;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>The limits of a driven session's container (ADR 0039); null on a preset that does not run one.</summary>
/// <param name="CheckRetries">Times a failed repository check or acceptance criterion goes back to the session before it ends <c>checks_failed</c>.</param>
/// <param name="GoalReminderMinutes">The session is sent its goal and criteria again after this many minutes; 0: never.</param>
/// <param name="GoalReminderTurns">Also after this many assistant messages; null: by minutes only.</param>
public sealed record DrivenPreset(int MaxMinutes = 45, int NoProgressMinutes = 10, int MaxTurns = 80, long? MaxTokens = null, int MemoryMb = 4096,
    double Cpus = 2, int Pids = 512, IReadOnlyList<string>? Allow = null, int MaxContinuations = 2, int CheckRetries = 2, int GoalReminderMinutes = 60, int? GoalReminderTurns = null);

/// <summary>The preset's <c>driven</c> limits, set per server. A key left out keeps the preset's value; the preset's allowlist stays the preset's
/// (<c>network.allow</c> adds hosts). Prompts and permissions are not here: they are what Prompt CI evaluates.</summary>
public sealed record DrivenLimits(int? MaxMinutes = null, int? NoProgressMinutes = null, int? MaxTurns = null, long? MaxTokens = null, int? MemoryMb = null,
    double? Cpus = null, int? Pids = null, int? MaxContinuations = null, int? CheckRetries = null, int? GoalReminderMinutes = null, int? GoalReminderTurns = null) : IValidatableObject
{
    public DrivenPreset Apply(DrivenPreset preset) => preset with
    {
        MaxMinutes = MaxMinutes ?? preset.MaxMinutes,
        NoProgressMinutes = NoProgressMinutes ?? preset.NoProgressMinutes,
        MaxTurns = MaxTurns ?? preset.MaxTurns,
        MaxTokens = MaxTokens ?? preset.MaxTokens,
        MemoryMb = MemoryMb ?? preset.MemoryMb,
        Cpus = Cpus ?? preset.Cpus,
        Pids = Pids ?? preset.Pids,
        MaxContinuations = MaxContinuations ?? preset.MaxContinuations,
        CheckRetries = CheckRetries ?? preset.CheckRetries,
        GoalReminderMinutes = GoalReminderMinutes ?? preset.GoalReminderMinutes,
        GoalReminderTurns = GoalReminderTurns ?? preset.GoalReminderTurns,
    };

    /// <summary>Throws an invalid_request error naming the key when a value is outside what the preset schema and the container template accept.</summary>
    public void Validate() => SettingsValidation.ThrowIfFailed(new DrivenLimitsValidator().Validate(null, this), "driven.limits",
        "Fix driven.limits in the profile; docs/guide/driven.md lists the keys.");

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext) => SettingsValidation.Results(Problems());

    private IEnumerable<string> Problems()
    {
        if (MaxMinutes is < 1) yield return "max_minutes must be at least 1";
        if (NoProgressMinutes is < 1) yield return "no_progress_minutes must be at least 1";
        if (MaxTurns is < 1) yield return "max_turns must be at least 1";
        if (MaxTokens is < 1) yield return "max_tokens must be at least 1";
        if (MemoryMb is < 256 or > 65_536) yield return "memory_mb must be 256 to 65536";
        if (Cpus is < 0.1 or > 32) yield return "cpus must be 0.1 to 32";
        if (Pids is < 16 or > 4096) yield return "pids must be 16 to 4096";
        if (MaxContinuations is < 0 or > 5) yield return "max_continuations must be 0 to 5";
        if (CheckRetries is < 0 or > 5) yield return "check_retries must be 0 to 5";
        if (GoalReminderMinutes is < 0) yield return "goal_reminder_minutes must be 0 (off) or more";
        if (GoalReminderTurns is < 1) yield return "goal_reminder_turns must be at least 1";
    }
}

/// <summary><c>driven.limits</c>' validator, source-generated (ADR 0060).</summary>
[OptionsValidator]
public sealed partial class DrivenLimitsValidator : IValidateOptions<DrivenLimits>;
