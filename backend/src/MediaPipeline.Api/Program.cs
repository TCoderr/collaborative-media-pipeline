using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using MediaPipeline.Api.Endpoints;
using MediaPipeline.Api.Health;
using MediaPipeline.Api.Http;
using MediaPipeline.Api.Realtime;
using MediaPipeline.Infrastructure;
using MediaPipeline.Infrastructure.Messaging;
using MediaPipeline.Infrastructure.Observability;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;

// ─────────────────────────────────────────────────────────────────────────────
// Quire API.
//
// A modular monolith: projects, assets, versions, reviews and processing are
// separate modules behind one deployable, one schema and one transaction
// boundary. The only process split is the media worker, which earns its
// separation through a different resource profile and a different scaling signal.
// ─────────────────────────────────────────────────────────────────────────────

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try { return (await probe.GetAsync("http://127.0.0.1:8080/api/v1/health/live").ConfigureAwait(false)).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { return 1; }
}
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 27L * 1024 * 1024);

builder.AddMediaPipelineTelemetry("collaborative-media-pipeline-api");
builder.Services.AddMediaPipelineInfrastructure(builder.Configuration);

// ASP.NET Core spans are added here rather than in the shared setup because the
// worker has no HTTP server to instrument.
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddAspNetCoreInstrumentation(o =>
{
    // Health probes fire constantly and would drown the useful traces.
    o.Filter = context => !context.Request.Path.StartsWithSegments("/health");
}));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "collaborative-media-pipeline.session";
        options.Cookie.HttpOnly = true;                       // unreadable from script
        options.Cookie.SameSite = SameSiteMode.Lax;           // survives top-level navigation, blocks cross-site POST
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // This is an API: answer with status codes, never a redirect to a login page.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

var keyPath = builder.Configuration["DataProtection:KeyPath"];
var keyBlob = builder.Configuration["DataProtection:BlobUri"];
var keyVault = builder.Configuration["DataProtection:KeyVaultKeyUri"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("collaborative-media-pipeline");
if (!string.IsNullOrWhiteSpace(keyBlob) && !string.IsNullOrWhiteSpace(keyVault))
{
    var credential = new Azure.Identity.DefaultAzureCredential();
    dataProtection.PersistKeysToAzureBlobStorage(new Uri(keyBlob), credential)
        .ProtectKeysWithAzureKeyVault(new Uri(keyVault), credential);
}
else if (!string.IsNullOrWhiteSpace(keyPath))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
else if (!builder.Environment.IsDevelopment() && !args.Contains("--migrate", StringComparer.Ordinal))
    throw new InvalidOperationException("A persistent shared data-protection key store must be configured.");
builder.Services.AddAuthorization();

// Double-submit CSRF defence on top of SameSite: the token arrives in a header the
// browser will not attach to a cross-site request automatically.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "collaborative-media-pipeline.csrf";
    options.Cookie.HttpOnly = false; // the SPA must read it to echo it back
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 32 * 1024;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton<ProjectSubscriptions>();
builder.Services.AddSingleton<RealtimeNotificationConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RealtimeNotificationConsumer>());

// The API drains the outbox too, so a single-process local stack still delivers work.
builder.Services.AddHostedService<OutboxPublisher>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"])
    .AddCheck<StorageReadinessCheck>("storage", tags: ["ready"])
    .AddCheck<BrokerLivenessCheck>("broker", failureStatus: HealthStatus.Degraded, tags: ["diagnostic"]);

// Same-origin by default; a configured origin list is only for split-host development.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length == 0)
    {
        // No cross-origin access at all.
        policy.WithOrigins("https://localhost").DisallowCredentials();
        return;
    }

    policy.WithOrigins(allowedOrigins)
        .AllowCredentials()                      // cookies must be allowed to travel
        .WithHeaders("Content-Type", "X-CSRF-TOKEN", "Idempotency-Key", "If-Match")
        .WithMethods("GET", "POST", "PATCH", "DELETE")
        .WithExposedHeaders("ETag");
}));

// Limits are configuration, not constants: production tunes them per environment, and the
// integration suite raises them so the limiter does not throttle the tests themselves.
var authLimit = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 10);
var uploadLimit = builder.Configuration.GetValue("RateLimits:UploadPerMinute", 60);
var globalLimit = builder.Configuration.GetValue("RateLimits:GlobalPerMinute", 600);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Authentication, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitioning.KeyFor(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = authLimit, Window = TimeSpan.FromMinutes(1) }));

    options.AddPolicy(RateLimitPolicies.Upload, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitioning.KeyFor(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = uploadLimit, Window = TimeSpan.FromMinutes(1) }));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitioning.KeyFor(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = globalLimit, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddOpenApi();

// Behind Front Door / Container Apps the client IP and scheme arrive in headers.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Keep loopback trust only. Configure explicit known proxies for any remote ingress.
});

// Multipart bodies are read section by section; the ceiling is enforced by the
// media policy while streaming, this is just a backstop.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 64L * 1024 * 1024;
});

var app = builder.Build();

// Migration mode: apply the schema and exit, without ever opening a listener.
//
// Deployments run this as a one-off task before rolling out replicas. Letting
// each replica migrate on start means N instances racing the same DDL during
// every deploy; EF's migration lock would serialise them, but the rollout would
// still be gated on whichever instance won. See docs/decisions/0006.
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    using var migrationScope = app.Services.CreateScope();
    var database = migrationScope.ServiceProvider
        .GetRequiredService<MediaPipeline.Application.Persistence.MediaPipelineDbContext>();

    app.Logger.LogInformation("event=migration_started");
    await database.Database.MigrateAsync().ConfigureAwait(false);
    app.Logger.LogInformation("event=migration_completed");
    return 0;
}

app.UseForwardedHeaders();
app.UseMiddleware<ErrorHandlingMiddleware>();

app.Use(async (context, next) =>
{
    // Security headers on every response. The API serves JSON and private media only,
    // so the policy can be maximally restrictive.
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

    if (!app.Environment.IsDevelopment())
    {
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }

    await next().ConfigureAwait(false);
});

// Front Door origin lock. When an expected id is configured, a request must
// carry the X-Azure-FDID header Front Door stamps with the profile's id.
//
// Defence in depth behind the internal API ingress and restricted frontend origin.
// A profile header by itself is not a network boundary. Health probes are exempt.
var expectedFrontDoorId = builder.Configuration["FrontDoor:ExpectedId"];
if (!string.IsNullOrWhiteSpace(expectedFrontDoorId))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var presented = context.Request.Headers["X-Azure-FDID"].ToString();
        if (!string.Equals(presented, expectedFrontDoorId, StringComparison.Ordinal))
        {
            app.Logger.LogWarning("event=origin_access_blocked path={RequestPath}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next().ConfigureAwait(false);
    });
}

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Antiforgery validation for unsafe verbs, applied centrally so a new endpoint is
// protected by default rather than by remembering to opt in.
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var unsafeMethod = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    // Three exemptions, each for a reason rather than for convenience:
    //
    //  - login and register are pre-session: there is no identity to forge a request as
    //    yet, and both are rate-limited to blunt credential stuffing;
    //  - the SignalR handshake is a POST to /negotiate that no browser client can attach a
    //    token to. It is protected instead by SameSite=Lax (a cross-site POST carries no
    //    session cookie, so it arrives unauthenticated and is rejected) and by the hub
    //    re-checking project membership on every subscribe.
    var path = context.Request.Path;
    var exempt = path.StartsWithSegments("/api/v1/auth/login")
        || path.StartsWithSegments("/api/v1/auth/register")
        || path.StartsWithSegments("/hubs");

    if (unsafeMethod && !exempt)
    {
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(
                new ErrorEnvelope { Code = "CSRF_TOKEN_INVALID", Message = "Missing or invalid CSRF token." },
                JsonDefaults.Options)).ConfigureAwait(false);
            return;
        }
    }

    await next().ConfigureAwait(false);
});

app.MapAuthEndpoints();
app.MapProjectEndpoints();
app.MapAssetEndpoints();
app.MapArchiveEndpoints();
MediaPipeline.Api.Health.OperationsEndpoints.MapOperationsEndpoints(app);

app.MapHub<CollaborationHub>("/hubs/collaboration");

MediaPipeline.Api.Health.HealthEndpoints.MapHealthEndpoints(app);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await app.RunAsync().ConfigureAwait(false);
return 0;

namespace MediaPipeline.Api
{
    /// <summary>
    /// Named entry-point marker for <c>WebApplicationFactory</c>. The implicit top-level
    /// <c>Program</c> cannot be used: the worker generates one too, and the integration test
    /// assembly references both.
    /// </summary>
    public sealed class ApiEntryPoint;
}

/// <summary>
/// Rate-limit partitioning: the signed-in user where there is one, otherwise the client IP.
/// Keyed on identity first so one busy office network cannot exhaust everyone's budget.
/// </summary>
internal static class RateLimitPartitioning
{
    public static string KeyFor(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? $"user:{context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value}"
            : $"ip:{context.Connection.RemoteIpAddress}";
}
