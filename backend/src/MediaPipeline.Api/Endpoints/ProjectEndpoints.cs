using System.Security.Claims;
using MediaPipeline.Api.Http;
using MediaPipeline.Application.Assets;
using MediaPipeline.Application.Projects;
using MediaPipeline.Domain.Projects;

namespace MediaPipeline.Api.Endpoints;

/// <summary>
/// Project and membership routes. Every handler passes the caller's id into the service and
/// the service resolves access; no endpoint decides authorisation for itself.
/// </summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/projects").WithTags("Projects").RequireAuthorization();

        group.MapGet("/", async (
            ClaimsPrincipal principal,
            ProjectService projects,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var page = await projects.ListAsync(principal.UserId(), RequestHelpers.PageFrom(request), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(page);
        })
        .WithSummary("Lists projects the caller is a member of.");

        group.MapPost("/", async (
            CreateProjectRequest body,
            ClaimsPrincipal principal,
            ProjectService projects,
            CancellationToken cancellationToken) =>
        {
            body.Validated();
            var project = await projects.CreateAsync(principal.UserId(), body.Name, body.Description, cancellationToken)
                .ConfigureAwait(false);
            return Results.Created($"/api/v1/projects/{project.Ref}", project);
        })
        .WithSummary("Creates a project with the caller as owner.");

        group.MapGet("/{projectRef}", async (
            string projectRef,
            ClaimsPrincipal principal,
            ProjectService projects,
            CancellationToken cancellationToken) =>
            Results.Ok(await projects.GetDetailAsync(principal.UserId(), projectRef, cancellationToken).ConfigureAwait(false)))
        .WithSummary("Returns a project with its membership.");

        group.MapPatch("/{projectRef}", async (
            string projectRef,
            UpdateProjectRequest body,
            ClaimsPrincipal principal,
            ProjectService projects,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            body.Validated();
            var project = await projects.UpdateAsync(
                principal.UserId(), projectRef, body.Name, body.Description,
                RequestHelpers.IfMatch(request), cancellationToken).ConfigureAwait(false);
            return Results.Ok(project);
        })
        .WithSummary("Updates project metadata. Send If-Match to detect concurrent edits.");

        group.MapPost("/{projectRef}/status", async (
            string projectRef,
            ChangeProjectStatusRequest body,
            ClaimsPrincipal principal,
            ProjectService projects,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            body.Validated();
            var status = RequestHelpers.ParseEnum<ProjectStatus>(body.Status, "INVALID_PROJECT_STATUS");
            var project = await projects.ChangeStatusAsync(
                principal.UserId(), projectRef, status, RequestHelpers.IfMatch(request), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(project);
        })
        .WithSummary("Moves a project between Draft, Active and Archived.");

        group.MapPost("/{projectRef}/members", async (
            string projectRef,
            AddMemberRequest body,
            ClaimsPrincipal principal,
            ProjectService projects,
            CancellationToken cancellationToken) =>
        {
            body.Validated();
            var role = RequestHelpers.ParseEnum<ProjectRole>(body.Role, "INVALID_PROJECT_ROLE");
            var project = await projects.AddMemberAsync(principal.UserId(), projectRef, body.Email, role, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(project);
        })
        .WithSummary("Adds a collaborator by email.");

        group.MapPatch("/{projectRef}/members/{userRef}", async (
            string projectRef,
            string userRef,
            ChangeMemberRoleRequest body,
            ClaimsPrincipal principal,
            ProjectService projects,
            CancellationToken cancellationToken) =>
        {
            body.Validated();
            var role = RequestHelpers.ParseEnum<ProjectRole>(body.Role, "INVALID_PROJECT_ROLE");
            var project = await projects.ChangeMemberRoleAsync(principal.UserId(), projectRef, userRef, role, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(project);
        })
        .WithSummary("Changes a collaborator's role. Ownership is transferred separately.");

        group.MapDelete("/{projectRef}/members/{userRef}", async (
            string projectRef,
            string userRef,
            ClaimsPrincipal principal,
            ProjectService projects,
            CancellationToken cancellationToken) =>
            Results.Ok(await projects.RemoveMemberAsync(principal.UserId(), projectRef, userRef, cancellationToken)
                .ConfigureAwait(false)))
        .WithSummary("Removes a collaborator. The owner cannot be removed.");

        group.MapPost("/{projectRef}/owner", async (
            string projectRef,
            TransferOwnershipRequest body,
            ClaimsPrincipal principal,
            ProjectService projects,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            body.Validated();
            var project = await projects.TransferOwnershipAsync(
                principal.UserId(), projectRef, body.NewOwnerRef, RequestHelpers.IfMatch(request), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(project);
        })
        .WithSummary("Transfers ownership to another member, demoting the previous owner to Editor.");

        group.MapGet("/{projectRef}/assets", async (
            string projectRef,
            ClaimsPrincipal principal,
            AssetService assets,
            HttpRequest request,
            CancellationToken cancellationToken) =>
            Results.Ok(await assets.ListAssetsAsync(
                principal.UserId(), projectRef, RequestHelpers.PageFrom(request), cancellationToken).ConfigureAwait(false)))
        .WithSummary("Lists a project's assets with their current version.");
    }
}
