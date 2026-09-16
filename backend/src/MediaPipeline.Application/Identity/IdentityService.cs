using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaPipeline.Application.Identity;

public sealed record AuthenticatedUser(Guid Id, string PublicRef, string Email, string DisplayName);

/// <summary>
/// Registration and credential verification. Session issuance itself belongs to the API
/// (it owns the cookie), so this service stays transport-agnostic.
/// </summary>
public sealed class IdentityService(
    MediaPipelineDbContext db,
    IPasswordHasher passwordHasher,
    IClock clock,
    ILogger<IdentityService> logger)
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 256;

    public async Task<AuthenticatedUser> RegisterAsync(string email, string displayName, string password, CancellationToken cancellationToken)
    {
        ValidatePassword(password);

        var normalised = User.NormaliseEmail(email);
        var user = User.Create(normalised, displayName, passwordHasher.Hash(password), clock.UtcNow);

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // Do not confirm whether the address exists - that would be an account oracle.
            db.ChangeTracker.Clear();
            throw MediaPipelineException.Conflict("REGISTRATION_FAILED", "That account could not be created.");
        }

        logger.LogInformation("event=user_registered user_ref={UserRef}", user.PublicRef);
        return new AuthenticatedUser(user.Id, user.PublicRef, user.Email, user.DisplayName);
    }

    /// <summary>
    /// Verifies credentials. Returns null for both "no such user" and "wrong password", and
    /// always performs a hash comparison so the two cases cost the same wall-clock time.
    /// </summary>
    public async Task<AuthenticatedUser?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalised = User.NormaliseEmail(email ?? string.Empty);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalised, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            _ = passwordHasher.Verify(DummyHashFor(passwordHasher), password ?? string.Empty);
            return null;
        }

        var (verified, needsRehash) = passwordHasher.Verify(user.PasswordHash, password ?? string.Empty);
        if (!verified)
        {
            logger.LogInformation("event=login_failed reason=invalid_credentials");
            return null;
        }

        if (needsRehash)
        {
            user.SetPasswordHash(passwordHasher.Hash(password!), clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("event=login_succeeded user_ref={UserRef}", user.PublicRef);
        return new AuthenticatedUser(user.Id, user.PublicRef, user.Email, user.DisplayName);
    }

    public async Task<AuthenticatedUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new AuthenticatedUser(u.Id, u.PublicRef, u.Email, u.DisplayName))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return user;
    }

    public async Task<Guid?> FindIdByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalised = User.NormaliseEmail(email ?? string.Empty);
        return await db.Users.AsNoTracking()
            .Where(u => u.Email == normalised)
            .Select(u => (Guid?)u.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            throw MediaPipelineException.Validation(
                "WEAK_PASSWORD", $"Password must be at least {MinPasswordLength} characters.");
        }

        if (password.Length > MaxPasswordLength)
        {
            throw MediaPipelineException.Validation(
                "PASSWORD_TOO_LONG", $"Password must be at most {MaxPasswordLength} characters.");
        }
    }

    /// <summary>
    /// A real hash of a value nobody holds, generated once at startup. Verifying against it
    /// makes an unknown-account login cost the same wall-clock time as a wrong-password
    /// login, so response timing cannot be used to enumerate accounts.
    /// </summary>
    private static string DummyHashFor(IPasswordHasher hasher) =>
        s_dummyHash ??= hasher.Hash(Guid.NewGuid().ToString("N"));

    private static string? s_dummyHash;
}
