namespace MediaPipeline.Application.Abstractions;

/// <summary>
/// The kinds of failure the application layer distinguishes. Each maps to exactly one
/// HTTP status at the edge, so no handler has to invent status codes.
/// </summary>
public enum ErrorKind
{
    Validation,
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    RuleViolation,
    DependencyUnavailable,
    Internal,
}

/// <summary>
/// An expected failure carrying a stable machine-readable code. Thrown for control flow
/// that callers are meant to handle; unexpected faults stay as ordinary exceptions.
/// </summary>
public class MediaPipelineException : Exception
{
    public MediaPipelineException(ErrorKind kind, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Code = code;
    }

    public ErrorKind Kind { get; }
    public string Code { get; }

    public static MediaPipelineException NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static MediaPipelineException Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static MediaPipelineException Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static MediaPipelineException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static MediaPipelineException Rule(string code, string message) => new(ErrorKind.RuleViolation, code, message);
    public static MediaPipelineException Dependency(string code, string message, Exception? inner = null) =>
        new(ErrorKind.DependencyUnavailable, code, message, inner);
}
