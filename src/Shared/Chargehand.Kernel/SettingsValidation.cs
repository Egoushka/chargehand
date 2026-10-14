using System.ComponentModel.DataAnnotations;
using Chargehand.Contracts;
using Microsoft.Extensions.Options;

namespace Chargehand.Kernel;

/// <summary>How a module refuses its profile section (ADR 0060): the section's source-generated validator (<c>[OptionsValidator]</c>) runs
/// the section's <see cref="IValidatableObject"/> rules, and its failures, in order, become one invalid_request error under the section's key.</summary>
public static class SettingsValidation
{
    /// <summary>The problems as results with no member names, which the generated validator reports word for word.</summary>
    public static IEnumerable<ValidationResult> Results(IEnumerable<string> problems) => problems.Select(problem => new ValidationResult(problem));

    /// <summary>The failures of a validator's result; empty when it succeeded.</summary>
    public static IEnumerable<string> Problems(ValidateOptionsResult result) => result.Failed ? result.Failures ?? [] : [];

    /// <summary>Throws <c>section: first; second</c> with <paramref name="action"/> as the hint when the validator failed.</summary>
    public static void ThrowIfFailed(ValidateOptionsResult result, string section, string action)
    {
        if (result.Failed)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"{section}: {string.Join("; ", Problems(result))}", action);
    }
}
