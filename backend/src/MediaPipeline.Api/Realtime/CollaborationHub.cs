using System.Security.Claims;
using MediaPipeline.Application.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MediaPipeline.Api.Realtime;

/// <summary>
/// The typed contract clients implement. Declaring it stops internal EF entities from
/// leaking onto the wire: the hub can only send these shapes.
/// </summary>
public interface ICollaborationClient
{
    Task ProjectEvent(RealtimeEnvelope envelope);
    Task SubscriptionChanged(string projectRef, bool subscribed);
}

/// <summary>
/// What a connected collaborator receives. <see cref="StateSequence"/> lets the client drop
/// an event older than the state it already holds, so out-of-order arrival is harmless.
/// </summary>
public sealed record RealtimeEnvelope(
    string EventType,
    string ProjectRef,
    long StateSequence,
    DateTimeOffset OccurredAt,
    object Payload);

/// <summary>
/// Project-scoped collaboration hub.
/// <para>
/// Two authorisation gates, deliberately separate: <c>[Authorize]</c> proves the connection
/// belongs to a signed-in user, and <see cref="SubscribeToProject"/> re-checks membership for
/// the specific project being joined. Being connected grants nothing.
/// </para>
/// <para>
/// The group name is derived server-side from the project's internal id. A client string is
/// never passed to <c>AddToGroupAsync</c>, so guessing a reference cannot join a group.
/// </para>
/// </summary>
[Authorize]
public sealed class CollaborationHub(
    ProjectAccessService access,
    ProjectSubscriptions subscriptions,
    ILogger<CollaborationHub> logger) : Hub<ICollaborationClient>
{
    /// <summary>The only place a SignalR group name is constructed.</summary>
    public static string GroupFor(Guid projectId) => $"project:{projectId:N}";

    public async Task SubscribeToProject(string projectRef)
    {
        var userId = RequireUserId();

        var resolved = await access.TryResolveAsync(userId, projectRef, Context.ConnectionAborted).ConfigureAwait(false);
        if (resolved is null)
        {
            // Refused without saying whether the project exists.
            logger.LogWarning(
                "event=hub_subscribe_denied connection_id={ConnectionId} project_ref={ProjectRef}",
                Context.ConnectionId, projectRef);

            throw new HubException("You do not have access to that project.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(resolved.ProjectId), Context.ConnectionAborted)
            .ConfigureAwait(false);

        logger.LogInformation(
            "event=hub_subscribed connection_id={ConnectionId} project_ref={ProjectRef} role={ProjectRole}",
            Context.ConnectionId, resolved.ProjectRef, resolved.Role);

        subscriptions.Add(resolved.ProjectId, Context.ConnectionId, userId);
        await Clients.Caller.SubscriptionChanged(resolved.ProjectRef, true).ConfigureAwait(false);
    }

    public async Task UnsubscribeFromProject(string projectRef)
    {
        var userId = RequireUserId();

        var resolved = await access.TryResolveAsync(userId, projectRef, Context.ConnectionAborted).ConfigureAwait(false);
        if (resolved is null)
        {
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(resolved.ProjectId), Context.ConnectionAborted)
            .ConfigureAwait(false);

        subscriptions.Remove(resolved.ProjectId, Context.ConnectionId);
        await Clients.Caller.SubscriptionChanged(resolved.ProjectRef, false).ConfigureAwait(false);
    }

    public override Task OnConnectedAsync()
    {
        logger.LogInformation("event=hub_connected connection_id={ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // Group membership dies with the connection. A reconnecting client re-subscribes and
        // then re-reads authoritative state from the API, so nothing depends on the socket
        // having stayed up.
        subscriptions.Disconnect(Context.ConnectionId);
        logger.LogInformation("event=hub_disconnected connection_id={ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private Guid RequireUserId()
    {
        var value = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(value, out var userId))
        {
            throw new HubException("Not authenticated.");
        }

        return userId;
    }
}
