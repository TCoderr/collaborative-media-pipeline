using System.Security.Claims;
using MediaPipeline.Api.Http;
using MediaPipeline.Application.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MediaPipeline.Api.Endpoints;

/// <summary>
/// Session endpoints.
/// <para>
/// Sessions are cookie-based: the browser sends the cookie on the SignalR WebSocket upgrade
/// automatically, which a bearer token in local storage cannot do without extra plumbing -
/// and the cookie is HttpOnly, so a script injection cannot read it. CSRF is handled
/// explicitly instead, with SameSite plus an antiforgery header on every unsafe request.
/// </para>
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapGet("/csrf", (IAntiforgery antiforgery, HttpContext context) =>
        {
            // Issues the cookie/header pair the SPA echoes on unsafe requests.
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
        })
        .WithSummary("Issues an antiforgery token for unsafe requests.")
        .AllowAnonymous();

        group.MapPost("/register", async (
            RegisterRequest request,
            IdentityService identity,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            request.Validated();

            var user = await identity.RegisterAsync(request.Email, request.DisplayName, request.Password, cancellationToken)
                .ConfigureAwait(false);

            await SignInAsync(context, user).ConfigureAwait(false);
            return Results.Created($"/api/v1/auth/session", ToView(user));
        })
        .RequireRateLimiting(RateLimitPolicies.Authentication)
        .WithSummary("Creates an account and starts a session.")
        .AllowAnonymous();

        group.MapPost("/login", async (
            LoginRequest request,
            IdentityService identity,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            request.Validated();

            var user = await identity.AuthenticateAsync(request.Email, request.Password, cancellationToken)
                .ConfigureAwait(false);

            if (user is null)
            {
                // One message for both "no such account" and "wrong password": telling them
                // apart would turn this endpoint into an account-enumeration oracle.
                return Results.Json(
                    new ErrorEnvelope { Code = "INVALID_CREDENTIALS", Message = "Email or password is incorrect." },
                    JsonDefaults.Options,
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            await SignInAsync(context, user).ConfigureAwait(false);
            return Results.Ok(ToView(user));
        })
        .RequireRateLimiting(RateLimitPolicies.Authentication)
        .WithSummary("Starts a session.")
        .AllowAnonymous();

        group.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithSummary("Ends the current session.")
        .RequireAuthorization();

        // Anonymous by design. This is how a client decides which surface to
        // show, so it must answer rather than fail: a 401 here would put a red
        // error in the console of every visitor who is simply not signed in yet.
        // It leaks nothing - an anonymous caller learns only that they are
        // anonymous, which they already knew.
        group.MapGet("/session", async (
            ClaimsPrincipal principal,
            IdentityService identity,
            CancellationToken cancellationToken) =>
        {
            if (principal.Identity?.IsAuthenticated != true)
            {
                return Results.Ok(new { authenticated = false });
            }

            var user = await identity.FindByIdAsync(principal.UserId(), cancellationToken).ConfigureAwait(false);

            // Authenticated cookie, but the account is gone: a stale session.
            return user is null
                ? Results.Ok(new { authenticated = false })
                : Results.Ok(ToView(user));
        })
        .WithSummary("Returns the signed-in account, or authenticated=false when there is no session.")
        .AllowAnonymous();
    }

    private static async Task SignInAsync(HttpContext context, AuthenticatedUser user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim("ref", user.PublicRef),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7),
                AllowRefresh = true,
            }).ConfigureAwait(false);
    }

    private static object ToView(AuthenticatedUser user) =>
        new { authenticated = true, userRef = user.PublicRef, user.Email, user.DisplayName };
}

/// <summary>Rate-limit policy names, so endpoints cannot reference a policy that does not exist.</summary>
public static class RateLimitPolicies
{
    /// <summary>Tight limit on credential endpoints - the ones worth brute-forcing.</summary>
    public const string Authentication = "auth";

    /// <summary>Bounded limit on uploads, which are the most expensive requests the API serves.</summary>
    public const string Upload = "upload";

    /// <summary>General ceiling on everything else.</summary>
    public const string Api = "api";
}
