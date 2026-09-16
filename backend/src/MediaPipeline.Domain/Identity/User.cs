using MediaPipeline.Domain.Common;

namespace MediaPipeline.Domain.Identity;

/// <summary>
/// A collaborator account. Deliberately minimal: Quire is a collaboration platform,
/// not an identity provider — no tenants, no billing, no SSO.
/// </summary>
public sealed class User
{
    public const int MaxEmailLength = 254;
    public const int MaxDisplayNameLength = 80;

    private User() { }

    public Guid Id { get; private set; }
    public string PublicRef { get; private set; } = string.Empty;

    /// <summary>Normalised (lower-cased, trimmed) — the uniqueness key.</summary>
    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c> — optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public static User Create(string email, string displayName, string passwordHash, DateTimeOffset now)
    {
        var normalisedEmail = NormaliseEmail(email);
        if (normalisedEmail.Length == 0 || normalisedEmail.Length > MaxEmailLength || !normalisedEmail.Contains('@', StringComparison.Ordinal))
        {
            throw new DomainException("INVALID_EMAIL", "A valid email address is required.");
        }

        var name = displayName.Trim();
        if (name.Length is 0 or > MaxDisplayNameLength)
        {
            throw new DomainException("INVALID_DISPLAY_NAME", $"Display name must be 1–{MaxDisplayNameLength} characters.");
        }

        return new User
        {
            Id = Guid.CreateVersion7(),
            PublicRef = Common.PublicRef.New(Common.PublicRef.UserPrefix),
            Email = normalisedEmail,
            DisplayName = name,
            PasswordHash = passwordHash,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void SetPasswordHash(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash;
        UpdatedAt = now;
    }

    public static string NormaliseEmail(string email) => email.Trim().ToLowerInvariant();
}
