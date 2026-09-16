using Microsoft.AspNetCore.Identity;

namespace MediaPipeline.Application.Abstractions;

/// <summary>
/// PBKDF2-HMAC-SHA512, 210k iterations, per-password salt - the ASP.NET Core V3 format.
/// Reused rather than hand-rolled: the format is versioned, constant-time compared, and
/// signals when a stored hash predates a parameter change.
/// </summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private sealed class HashSubject;

    private readonly PasswordHasher<HashSubject> _inner = new(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
    private static readonly HashSubject Subject = new();

    public string Hash(string password) => _inner.HashPassword(Subject, password);

    public (bool Verified, bool NeedsRehash) Verify(string hash, string password)
    {
        PasswordVerificationResult result;
        try
        {
            result = _inner.VerifyHashedPassword(Subject, hash, password);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // A stored hash that is not in the expected format - corrupted data, or a
            // placeholder used to equalise login timing. Fail closed: an unreadable hash
            // is a failed verification, never an unhandled 500 on the login path.
            return (false, false);
        }

        return result switch
        {
            PasswordVerificationResult.Success => (true, false),
            PasswordVerificationResult.SuccessRehashNeeded => (true, true),
            _ => (false, false),
        };
    }
}
