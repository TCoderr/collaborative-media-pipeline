using Microsoft.EntityFrameworkCore;

namespace MediaPipeline.Application.Persistence;

/// <summary>
/// Recognises the PostgreSQL error codes the application treats as control flow.
/// <para>
/// Matched on SQLSTATE via the exception's <c>SqlState</c> property, reached by reflection
/// so the application layer does not take a hard reference on the Npgsql driver types.
/// </para>
/// </summary>
public static class PostgresErrors
{
    public const string UniqueViolation = "23505";
    public const string ForeignKeyViolation = "23503";
    public const string CheckViolation = "23514";
    public const string SerializationFailure = "40001";
    public const string DeadlockDetected = "40P01";

    public static bool IsUniqueViolation(Exception exception) => HasSqlState(exception, UniqueViolation);

    public static bool IsForeignKeyViolation(Exception exception) => HasSqlState(exception, ForeignKeyViolation);

    public static bool IsSerializationFailure(Exception exception) =>
        HasSqlState(exception, SerializationFailure) || HasSqlState(exception, DeadlockDetected);

    /// <summary>Whether a unique violation came from a specific named index.</summary>
    public static bool IsUniqueViolationOn(Exception exception, string constraintName)
    {
        if (!IsUniqueViolation(exception))
        {
            return false;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var value = ReadProperty(current, "ConstraintName");
            if (string.Equals(value, constraintName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A concurrency token mismatch: another writer changed the row first.</summary>
    public static bool IsConcurrencyConflict(Exception exception) => exception is DbUpdateConcurrencyException;

    private static bool HasSqlState(Exception exception, string sqlState)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (string.Equals(ReadProperty(current, "SqlState"), sqlState, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadProperty(Exception exception, string propertyName) =>
        exception.GetType().GetProperty(propertyName)?.GetValue(exception) as string;
}
