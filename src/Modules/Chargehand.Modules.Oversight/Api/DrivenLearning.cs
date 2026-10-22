using System.ComponentModel.DataAnnotations;
using Chargehand.Kernel;
using Microsoft.Extensions.Options;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The learning loop (ADR 0045): each finished task leaves a record, the server reads the outcome of its pull request back from the forge, and <c>chargehand learn report</c> sums them up.
/// The records are always written; these keys tune the read-back, the optional retro and the optional failure autopsy.</summary>
/// <param name="Retro">After each batch, one cheap read-only run over the batch's records writes a short retro on the batch result. Off by default.</param>
/// <param name="RetroPreset">The preset the retro run uses; it must be read-only. Default <c>cheap</c>.</param>
/// <param name="PrDays">How long after a task finished its pull request is read again until it is merged or closed. Default 14.</param>
/// <param name="PrCheckMinutes">How often the server looks at the open ones. Default 60.</param>
/// <param name="Autopsy">For each task of a batch that ends failed or partial, one cheap read-only run over that task's record writes a short root-cause class into the learning log, and the
/// weekly job files a tracker item for the top clusters of it. Off by default.</param>
/// <param name="AutopsyPreset">The preset the autopsy run uses; it must be read-only. Default <c>cheap</c>.</param>
/// <param name="AutopsyClusterDays">The window the weekly job clusters autopsies over. Default 7.</param>
/// <param name="AutopsyClusterMin">The fewest autopsies of one class and repository in the window before the weekly job files it. Default 3.</param>
/// <param name="AutopsyClusterTop">The most clusters the weekly job files in one run, most frequent first. Default 3.</param>
public sealed record DrivenLearning(bool Retro = false, string RetroPreset = "cheap", int PrDays = 14, int PrCheckMinutes = 60, bool Autopsy = false, string AutopsyPreset = "cheap",
    int AutopsyClusterDays = 7, int AutopsyClusterMin = 3, int AutopsyClusterTop = 3) : IValidatableObject
{
    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    public void Validate() => SettingsValidation.ThrowIfFailed(new DrivenLearningValidator().Validate(null, this), "driven.learning",
        "Fix driven.learning in the profile; docs/guide/driven.md lists the keys.");

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext) => SettingsValidation.Results(Problems());

    private IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(RetroPreset)) yield return "retro_preset must not be empty";
        if (PrDays < 1) yield return "pr_days must be at least 1";
        if (PrCheckMinutes < 1) yield return "pr_check_minutes must be at least 1";
        if (string.IsNullOrWhiteSpace(AutopsyPreset)) yield return "autopsy_preset must not be empty";
        if (AutopsyClusterDays < 1) yield return "autopsy_cluster_days must be at least 1";
        if (AutopsyClusterMin < 1) yield return "autopsy_cluster_min must be at least 1";
        if (AutopsyClusterTop < 1) yield return "autopsy_cluster_top must be at least 1";
    }
}

/// <summary><c>driven.learning</c>'s validator, source-generated (ADR 0060).</summary>
[OptionsValidator]
public sealed partial class DrivenLearningValidator : IValidateOptions<DrivenLearning>;
