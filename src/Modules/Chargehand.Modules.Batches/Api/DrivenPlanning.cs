using System.ComponentModel.DataAnnotations;
using Chargehand.Kernel;
using Microsoft.Extensions.Options;

namespace Chargehand.Modules.Batches.Api;

/// <summary>The planning step before a batch (ADR 0049): one read-only run through the orchestrator's own pipeline, on a cheap preset, so no new model client or credential is involved.</summary>
/// <param name="Enabled">Plan every batch; a request's <c>driven.plan</c> overrides it either way. Off by default.</param>
/// <param name="Preset">The preset the run uses; it must be read-only. Default <c>cheap</c>.</param>
public sealed record DrivenPlanning(bool Enabled = false, string Preset = "cheap") : IValidatableObject
{
    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    public void Validate() => SettingsValidation.ThrowIfFailed(new DrivenPlanningValidator().Validate(null, this), "driven.planning",
        "Fix driven.planning in the profile; docs/guide/driven.md lists the keys.");

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext) => SettingsValidation.Results(Problems());

    private IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Preset)) yield return "preset must not be empty";
    }
}

/// <summary><c>driven.planning</c>'s validator, source-generated (ADR 0060).</summary>
[OptionsValidator]
public sealed partial class DrivenPlanningValidator : IValidateOptions<DrivenPlanning>;
