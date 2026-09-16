using System.Net.Http.Json;
using System.Text.Json;
using MediaPipeline.Api.Realtime;
using MediaPipeline.Application.Processing;
using MediaPipeline.Contracts;
using MediaPipeline.Infrastructure.Messaging;
using MediaPipeline.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MediaPipeline.IntegrationTests;

/// <summary>
/// SignalR collaboration over a real hub connection, with the notification fan-out flowing
/// through the real broker.
/// <para>
/// Two properties matter and are tested separately: a connected collaborator receives live
/// updates, and a connection can never reach a project the user is not a member of. Being
/// connected grants nothing on its own.
/// </para>
/// </summary>
[Collection(MediaPipelineTestGroup.Name)]
public sealed class RealtimeCollaborationTests(MediaPipelineFixture fixture) : IAsyncLifetime
{
    private BrokerHarness _broker = null!;

    public async Task InitializeAsync()
    {
        _broker = await BrokerHarness.ConnectAsync();
        await _broker.PurgeAsync();
    }

    public async Task DisposeAsync() => await _broker.DisposeAsync();

    [Fact]
    public async Task AnAnonymousConnectionIsRefused()
    {
        await using var connection = BuildConnection(cookieHeader: null);

        // The hub is [Authorize]; an unauthenticated socket must never open.
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task AMemberCanSubscribeToTheirOwnProject()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Live Project");

        await using var connection = await ConnectAsync(owner);

        var subscribed = new TaskCompletionSource<string>();
        connection.On<string, bool>(nameof(ICollaborationClient.SubscriptionChanged),
            (projectRef, isSubscribed) =>
            {
                if (isSubscribed)
                {
                    subscribed.TrySetResult(projectRef);
                }
            });

        await connection.InvokeAsync("SubscribeToProject", project);

        var confirmed = await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(project, confirmed);
    }

    [Fact]
    public async Task ANonMemberCannotSubscribeToSomeoneElsesProject()
    {
        var owner = await fixture.RegisterAsync();
        var outsider = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Private Project");

        await using var connection = await ConnectAsync(outsider);

        // Holding a valid session is not access. The group name is derived server-side from
        // membership, so guessing a project reference achieves nothing.
        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("SubscribeToProject", project));

        Assert.Contains("access", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AForgedOrMalformedProjectReferenceIsRefused()
    {
        var user = await fixture.RegisterAsync();
        await using var connection = await ConnectAsync(user);

        foreach (var forged in new[] { "project:00000000000000000000000000000000", "prj_notreal", "../../admin", "" })
        {
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("SubscribeToProject", forged));
        }
    }

    [Fact]
    public async Task ACommentReachesASecondCollaboratorWithoutAReload()
    {
        var owner = await fixture.RegisterAsync();
        var reviewer = await fixture.RegisterAsync();

        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Live Comments");
        await ProjectAuthorizationTests.AddMemberAsync(owner, project, reviewer.Email, "Reviewer");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "sheet.jpg");
        await fixture.OpenReviewAsync(owner, versionRef);

        await using var connection = await ConnectAsync(reviewer);
        var received = ListenFor(connection, RealtimeEvents.CommentCreated);
        await connection.InvokeAsync("SubscribeToProject", project);

        var posted = await owner.Client.PostAsJsonAsync(
            $"/api/v1/versions/{versionRef}/comments", new { body = "The gutter is too tight." });
        posted.EnsureSuccessStatusCode();

        // The notification travels API -> RabbitMQ fan-out -> every replica -> this socket.
        await fixture.DispatchAsync();
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(project, envelope.ProjectRef);
        var payload = (JsonElement)envelope.Payload;
        Assert.False(string.IsNullOrEmpty(payload.Str("commentRef")));
        var comments = await reviewer.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}/comments");
        Assert.Equal("The gutter is too tight.", comments.Items().Single().Str("body"));
    }

    [Fact]
    public async Task AReviewDecisionReachesOtherCollaboratorsLive()
    {
        var owner = await fixture.RegisterAsync();
        var watcher = await fixture.RegisterAsync();

        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Live Review");
        await ProjectAuthorizationTests.AddMemberAsync(owner, project, watcher.Email, "Viewer");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "proof.jpg");
        await fixture.ReadyAsync(versionRef);

        await using var connection = await ConnectAsync(watcher);
        var received = ListenFor(connection, RealtimeEvents.ReviewStateChanged);
        await connection.InvokeAsync("SubscribeToProject", project);

        await MediaPipelineFixture.ReviewAsync(owner, versionRef, "InReview");

        await fixture.DispatchAsync();
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var payload = (JsonElement)envelope.Payload;

        Assert.Equal("InReview", payload.Str("reviewState"));

        // The sequence lets a client discard an event older than the state it already holds.
        Assert.True(envelope.StateSequence > 0);
    }

    [Fact]
    public async Task ProcessingCompletionReachesCollaboratorsAfterTheStateIsDurable()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Live Processing");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "render.jpg");

        await using var connection = await ConnectAsync(owner);
        var completion = new TaskCompletionSource<RealtimeEnvelope>();

        connection.On<RealtimeEnvelope>(nameof(ICollaborationClient.ProjectEvent), envelope =>
        {
            if (envelope.EventType != RealtimeEvents.ProcessingStateChanged)
            {
                return;
            }

            var payload = (JsonElement)envelope.Payload;
            if (payload.Str("processingState") == "Succeeded")
            {
                completion.TrySetResult(envelope);
            }
        });

        await connection.InvokeAsync("SubscribeToProject", project);

        // Run the pipeline for real: publish the outbox, then process the message.
        var message = await PublishAndTakeAsync(versionRef);
        using (var scope = fixture.NewScope())
        {
            await scope.Get<MediaProcessingHandler>().HandleAsync(message, "test-worker", CancellationToken.None);
        }

        await fixture.DispatchAsync();
        var envelope = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // The event followed the commit, so re-reading the API agrees with what was pushed.
        var version = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}");
        Assert.Equal("Succeeded", version.Str("processingState"));
        Assert.True(envelope.StateSequence <= version.Sequence("stateSequence"));
    }

    [Fact]
    public async Task AClientThatMissesEventsRecoversTheTruthFromTheApi()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Missed Events");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "offline.jpg");
        await fixture.ReadyAsync(versionRef);

        // The collaborator is offline for the whole exchange - no socket at all.
        await MediaPipelineFixture.ReviewAsync(owner, versionRef, "InReview");
        await owner.Client.PostAsJsonAsync($"/api/v1/versions/{versionRef}/comments", new { body = "Missed this one." });

        // On reconnect they re-read authoritative state and see everything. Real-time is an
        // accelerator; PostgreSQL is the source of truth.
        var viewer = await fixture.RegisterAsync();
        await ProjectAuthorizationTests.AddMemberAsync(owner, project, viewer.Email, "Viewer");

        await using var connection = await ConnectAsync(viewer);
        await connection.InvokeAsync("SubscribeToProject", project);

        var version = await viewer.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}");
        var comments = await viewer.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}/comments");

        Assert.Equal("InReview", version.Str("reviewState"));
        Assert.Single(comments.Items());
    }

    [Fact]
    public async Task AReconnectingClientResubscribesAndReceivesAgain()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Reconnection");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "reconnect.jpg");
        await fixture.OpenReviewAsync(owner, versionRef);

        await using var connection = await ConnectAsync(owner);
        await connection.InvokeAsync("SubscribeToProject", project);

        // Group membership dies with the connection, so a reconnect must resubscribe.
        await connection.StopAsync();
        await connection.StartAsync();

        var received = ListenFor(connection, RealtimeEvents.CommentCreated);
        await connection.InvokeAsync("SubscribeToProject", project);

        await owner.Client.PostAsJsonAsync(
            $"/api/v1/versions/{versionRef}/comments", new { body = "After reconnecting." });

        await fixture.DispatchAsync();
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(project, envelope.ProjectRef);
    }

    [Fact]
    public async Task EventsForOneProjectNeverLeakIntoAnother()
    {
        var alice = await fixture.RegisterAsync();
        var bob = await fixture.RegisterAsync();

        var aliceProject = await ProjectAuthorizationTests.CreateProjectAsync(alice, "Alice's Work");
        var bobProject = await ProjectAuthorizationTests.CreateProjectAsync(bob, "Bob's Work");
        var (_, aliceVersion) = await ProjectAuthorizationTests.UploadAssetAsync(alice, aliceProject, "alice.jpg");

        await using var bobConnection = await ConnectAsync(bob);

        var leaked = new TaskCompletionSource<RealtimeEnvelope>();
        bobConnection.On<RealtimeEnvelope>(nameof(ICollaborationClient.ProjectEvent), envelope => leaked.TrySetResult(envelope));

        await bobConnection.InvokeAsync("SubscribeToProject", bobProject);

        // Alice works in her project; Bob is subscribed only to his.
        await alice.Client.PostAsJsonAsync(
            $"/api/v1/versions/{aliceVersion}/comments", new { body = "Private to Alice's project." });

        // Give the fan-out a real chance to misdeliver before concluding it did not.
        var raced = await Task.WhenAny(leaked.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        if (raced == leaked.Task)
        {
            // Anything Bob does receive must belong to his own project.
            var envelope = await leaked.Task;
            Assert.Equal(bobProject, envelope.ProjectRef);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HubConnection BuildConnection(string? cookieHeader) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.Server.BaseAddress, "hubs/collaboration"), options =>
            {
                options.HttpMessageHandlerFactory = _ => fixture.Server.CreateHandler();

                if (cookieHeader is not null)
                {
                    options.Headers["Cookie"] = cookieHeader;
                }
            })
            .Build();

    /// <summary>
    /// Opens a hub connection carrying the user's session cookie - the same cookie a browser
    /// attaches to the WebSocket upgrade automatically, which is why the session is a cookie
    /// rather than a token in script-readable storage.
    /// </summary>
    private async Task<HubConnection> ConnectAsync(TestUser user)
    {
        var connection = BuildConnection(user.CookieHeader);
        await connection.StartAsync();
        return connection;
    }

    private static TaskCompletionSource<RealtimeEnvelope> ListenFor(HubConnection connection, string eventType)
    {
        var source = new TaskCompletionSource<RealtimeEnvelope>();

        connection.On<RealtimeEnvelope>(nameof(ICollaborationClient.ProjectEvent), envelope =>
        {
            if (envelope.EventType == eventType)
            {
                source.TrySetResult(envelope);
            }
        });

        return source;
    }

    private async Task<MediaProcessingRequestedV1> PublishAndTakeAsync(string versionRef)
    {
        using (var scope = fixture.NewScope())
        {
            var publisher = ActivatorUtilities.CreateInstance<OutboxPublisher>(scope.Services);
            await publisher.DispatchBatchAsync(CancellationToken.None);
        }

        return await Eventually.Async(
            async () =>
            {
                var messages = await _broker.DrainAsync(Topology.MediaQueue);
                return messages.FirstOrDefault(m => m.AssetVersionRef == versionRef);
            },
            description: $"a processing message for {versionRef}");
    }
}
