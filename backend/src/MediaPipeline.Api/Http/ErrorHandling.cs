using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Domain.Common;

namespace MediaPipeline.Api.Http;

/// <summary>
/// The one error shape the API ever returns.
/// <para>
/// <c>code</c> is stable and machine-readable so clients branch on it instead of parsing
/// prose; <c>traceId</c> lets an operator find the request in the traces. Nothing else is
/// exposed - no exception type, no stack, no SQL, no storage key.
/// </para>
/// </summary>
public sealed record ErrorEnvelope
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}

/// <summary>
/// Terminal exception handler. Expected failures become their mapped status; anything else
/// becomes a 500 whose body says nothing beyond a trace id, while the full detail goes to
/// the structured log where only operators can see it.
/// </summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (MediaPipelineException ex)
        {
            await WriteAsync(context, MapStatus(ex.Kind), ex.Code, ex.Message, ex, logLevel: LogLevel.Information)
                .ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            // A domain-rule violation is a 422: the request was well-formed and authorised,
            // but the aggregate refused it.
            await WriteAsync(context, StatusCodes.Status422UnprocessableEntity, ex.Code, ex.Message, ex,
                logLevel: LogLevel.Information).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ex)
        {
            // Name the entities that lost the race. Without this an operator sees only
            // "0 rows affected" and cannot tell which aggregate was contended.
            var entities = string.Join(", ", ex.Entries.Select(e => $"{e.Metadata.Name}:{e.State}"));
            logger.LogWarning(ex, "event=concurrency_conflict entities={ConflictingEntities}", entities);

            await WriteAsync(context, StatusCodes.Status409Conflict, "CONCURRENT_MODIFICATION",
                "Someone else changed this while you were editing. Reload and try again.", ex,
                logLevel: LogLevel.Information).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, "MALFORMED_REQUEST",
                "The request could not be read.", ex, logLevel: LogLevel.Information).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client hung up. Nothing to report, and no response to write.
            logger.LogInformation("event=request_aborted path={RequestPath}", context.Request.Path);
        }
        catch (Exception ex)
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "INTERNAL_ERROR",
                "Something went wrong on our side.", ex, logLevel: LogLevel.Error).ConfigureAwait(false);
        }
    }

    private static int MapStatus(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.RuleViolation => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.DependencyUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private async Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        string message,
        Exception exception,
        LogLevel logLevel)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        logger.Log(logLevel, exception,
            "event=request_failed code={ErrorCode} status={StatusCode} method={HttpMethod} path={RequestPath} trace_id={TraceId}",
            code, status, context.Request.Method, context.Request.Path, traceId);

        if (context.Response.HasStarted)
        {
            // Too late to change the response; the log above is the record of what happened.
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(
            new ErrorEnvelope { Code = code, Message = message, TraceId = traceId },
            JsonDefaults.Options)).ConfigureAwait(false);
    }
}

/// <summary>Serialisation settings shared by responses and the SignalR hub.</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
