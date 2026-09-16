using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Contracts;

namespace MediaPipeline.Api.Http;

// Request bodies. Attributes cover shape and length; the domain still enforces every rule,
// because validation at the edge is a convenience, never the guarantee.

public sealed record RegisterRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(80, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;

    [Required, StringLength(256, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;
}

public sealed record LoginRequest
{
    [Required, StringLength(254)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(256)]
    public string Password { get; init; } = string.Empty;
}

public sealed record CreateProjectRequest
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; init; }
}

public sealed record UpdateProjectRequest
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; init; }
}

public sealed record ChangeProjectStatusRequest
{
    [Required, StringLength(32)]
    public string Status { get; init; } = string.Empty;
}

public sealed record AddMemberRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(32)]
    public string Role { get; init; } = string.Empty;
}

public sealed record ChangeMemberRoleRequest
{
    [Required, StringLength(32)]
    public string Role { get; init; } = string.Empty;
}

public sealed record TransferOwnershipRequest
{
    [Required, StringLength(64)]
    public string NewOwnerRef { get; init; } = string.Empty;
}

public sealed record ChangeReviewStateRequest
{
    [Required, StringLength(32)]
    public string ReviewState { get; init; } = string.Empty;
}

public sealed record CreateCommentRequest
{
    [StringLength(64)]
    public string? ParentRef { get; init; }
    [Required, StringLength(4000, MinimumLength = 1)]
    public string Body { get; init; } = string.Empty;
}

public sealed record ResolveCommentRequest
{
    public bool Resolved { get; init; } = true;
}

/// <summary>Validation, enum parsing and identity extraction shared by every endpoint.</summary>
public static class RequestHelpers
{
    /// <summary>
    /// Runs DataAnnotations and converts failures into the standard envelope. Endpoints call
    /// this explicitly so a forgotten attribute cannot silently disable validation.
    /// </summary>
    public static T Validated<T>(this T request) where T : notnull
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return request;
        }

        var first = results[0];
        throw MediaPipelineException.Validation(
            "VALIDATION_FAILED",
            first.ErrorMessage ?? "The request body is not valid.");
    }

    public static TEnum ParseEnum<TEnum>(string value, string code) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw MediaPipelineException.Validation(
            code, $"'{value}' is not a recognised {typeof(TEnum).Name}. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    /// <summary>The authenticated user's internal id, taken only from the validated principal.</summary>
    public static Guid UserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(value, out var id))
        {
            throw new MediaPipelineException(ErrorKind.Unauthenticated, "NOT_AUTHENTICATED", "You are not signed in.");
        }

        return id;
    }

    public static PageRequest PageFrom(HttpRequest request)
    {
        var limit = PageRequest.DefaultLimit;
        if (request.Query.TryGetValue("limit", out var rawLimit)
            && int.TryParse(rawLimit, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            limit = parsed;
        }

        var cursor = request.Query.TryGetValue("cursor", out var rawCursor) ? rawCursor.ToString() : null;
        return new PageRequest { Limit = limit, Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor };
    }

    /// <summary>Reads the optimistic-concurrency token a client echoes back on an update.</summary>
    public static string? IfMatch(HttpRequest request) =>
        request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.ToString() : null;

    public static string? IdempotencyKey(HttpRequest request) =>
        request.Headers.TryGetValue("Idempotency-Key", out var value) ? value.ToString() : null;
}
