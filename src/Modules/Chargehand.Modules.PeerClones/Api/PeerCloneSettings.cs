using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Kernel;
using Microsoft.Extensions.Options;

namespace Chargehand.Modules.PeerClones.Api;

/// <summary>
/// A public GitHub repository read by a shallow clone instead of a checkout under <c>repository_roots</c>: an account with
/// <c>public_clone</c> may send <c>context.repository.path</c> as <c>gh:owner/name</c>. The clone is made under <paramref name="Root"/>
/// (never intake's clones root), at <c>&lt;owner&gt;/&lt;name&gt;@&lt;commit&gt;</c>, and deleted when the run ends, whatever the
/// outcome. One clone runs at a time.
/// </summary>
/// <param name="Root">Where clones live, separate from intake's clones root. Created on first use.</param>
/// <param name="MaxRepositoryBytes">A repository whose GraphQL <c>diskUsage</c> is over this is refused before anything is cloned, at no
/// cost. Default 200 MB.</param>
/// <param name="MinFreeBytes">A clone is refused when <paramref name="Root"/>'s filesystem has less free than this. A starting value;
/// raise it for a busy cache. Default 5 GB.</param>
/// <param name="TokenSecret">Secret-store item holding a GitHub token sent as a bearer token for the size check and the clone; a public
/// repository still needs one, since GitHub's GraphQL API takes no anonymous calls.</param>
public sealed record PeerCloneSettings(string Root, long MaxRepositoryBytes = 200_000_000, long MinFreeBytes = 5_000_000_000, string? TokenSecret = null) : IValidatableObject
{
    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext) => SettingsValidation.Results(Problems());

    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Root)) yield return "root is required";
        else if (!Path.IsPathRooted(Root)) yield return "root must be an absolute path";
        if (MaxRepositoryBytes <= 0) yield return "max_repository_bytes must be above 0";
        if (MinFreeBytes <= 0) yield return "min_free_bytes must be above 0";
        if (string.IsNullOrWhiteSpace(TokenSecret)) yield return "token_secret is required: GitHub's GraphQL API takes no anonymous calls";
    }

    /// <summary>Throws an invalid_request error naming the problems, as the profile's other settings blocks do.</summary>
    public void Validate() => SettingsValidation.ThrowIfFailed(new PeerCloneSettingsValidator().Validate(null, this), "peer_clone",
        "Fix peer_clone in the profile; docs/guide/reference.md lists the fields.");
}

/// <summary><c>peer_clone</c>'s validator, source-generated (ADR 0060).</summary>
[OptionsValidator]
public sealed partial class PeerCloneSettingsValidator : IValidateOptions<PeerCloneSettings>;

/// <summary><c>peer_clone</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(PeerCloneSettings))]
public sealed partial class PeerCloneSettingsJsonContext : JsonSerializerContext;
