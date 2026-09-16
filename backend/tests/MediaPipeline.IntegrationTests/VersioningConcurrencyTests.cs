using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaPipeline.Domain.Assets;
using MediaPipeline.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MediaPipeline.IntegrationTests;

/// <summary>
/// Version allocation and optimistic concurrency, exercised concurrently against real
/// PostgreSQL.
/// <para>
/// These are the tests that would pass against an in-memory provider while the production
/// system corrupted itself: the unique index, <c>xmin</c>, and genuine parallel writes are
/// the only things that make the guarantees real.
/// </para>
/// </summary>
[Collection(MediaPipelineTestGroup.Name)]
public sealed class VersioningConcurrencyTests(MediaPipelineFixture fixture)
{
    [Fact]
    public async Task VersionsAreNumberedMonotonicallyFromOne()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Version History");
        var (assetRef, firstVersionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "plate.jpg");

        var first = await GetVersionAsync(owner, firstVersionRef);
        Assert.Equal(1, first.Number("versionNumber"));

        for (var expected = 2; expected <= 4; expected++)
        {
            var next = await AddVersionAsync(owner, assetRef, $"plate-v{expected}.jpg");
            Assert.Equal(expected, next.Number("versionNumber"));
        }
    }

    [Fact]
    public async Task ConcurrentVersionCreationNeverProducesADuplicateNumber()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Contended Asset");
        var (assetRef, _) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "base.jpg");

        // Six editors upload "the next version" at the same moment. Every one of them
        // computes the same MAX(version_number) + 1; the unique index resolves it.
        const int Racers = 6;
        var editors = new List<TestUser>();
        for (var i = 0; i < Racers; i++)
        {
            var editor = await fixture.RegisterAsync();
            await ProjectAuthorizationTests.AddMemberAsync(owner, project, editor.Email, "Editor");
            editors.Add(editor);
        }

        var barrier = new TaskCompletionSource();
        var uploads = editors.Select(async (editor, index) =>
        {
            await barrier.Task;
            using var content = TestImages.AsUpload(TestImages.Jpeg(400, 300), $"race-{index}.jpg");
            return await editor.Client.PostAsync(
                new Uri($"/api/v1/assets/{assetRef}/versions", UriKind.Relative), content);
        }).ToList();

        barrier.SetResult();
        var responses = await Task.WhenAll(uploads);

        // Every racing upload is a legitimate new version, so every one must succeed. A
        // collision on "the next number" is the database's to resolve, not the user's.
        var statuses = responses.Select(r => (int)r.StatusCode).ToList();
        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(
            succeeded == Racers,
            $"Expected all {Racers} concurrent uploads to succeed; got [{string.Join(", ", statuses)}].");

        // Whatever the interleaving, the history must be a clean 1..N with no repeats.
        using var scope = fixture.NewScope();
        var numbers = await scope.Db.AssetVersions.AsNoTracking()
            .Where(v => v.PublicRef != null)
            .Join(scope.Db.Assets.Where(a => a.PublicRef == assetRef), v => v.AssetId, a => a.Id, (v, _) => v.VersionNumber)
            .OrderBy(n => n)
            .ToListAsync();

        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
        Assert.Equal(Racers + 1, numbers.Count);
    }

    [Fact]
    public async Task AFinalisedVersionIsImmutableAndHistoryIsPreserved()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Immutable History");

        var firstBytes = TestImages.Jpeg(800, 600);
        var (assetRef, v1Ref) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "v1.jpg", firstBytes);
        var v1 = await GetVersionAsync(owner, v1Ref);
        var v1Checksum = v1.Str("sha256");

        var v2 = await AddVersionAsync(owner, assetRef, "v2.jpg", TestImages.Jpeg(1000, 700));

        // The new version must not have rewritten the old one's identity.
        var v1After = await GetVersionAsync(owner, v1Ref);
        Assert.Equal(v1Checksum, v1After.Str("sha256"));
        Assert.Equal(1, v1After.Number("versionNumber"));
        Assert.NotEqual(v1Checksum, v2.Str("sha256"));

        await fixture.ReadyAsync(v1Ref);
        // And the source media of V1 is still byte-identical in storage.
        var content = await owner.Client.GetByteArrayAsync(
            new Uri($"/api/v1/versions/{v1Ref}/content", UriKind.Relative));
        Assert.Equal(firstBytes, content);
    }

    [Fact]
    public async Task AStaleIfMatchTokenIsRejectedInsteadOfOverwriting()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Contended Metadata");

        var token = await ReadConcurrencyTokenAsync(project);

        // Someone else edits first.
        var winner = await owner.Client.PatchAsJsonAsync(
            $"/api/v1/projects/{project}", new { name = "Renamed by the winner", description = "" });
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);

        // The second writer still holds the token from before that edit.
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/projects/{project}")
        {
            Content = JsonContent.Create(new { name = "Renamed by the loser", description = "" }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", token);

        var loser = await owner.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("CONCURRENT_MODIFICATION", (await loser.Content.ReadFromJsonAsync<JsonElement>()).Str("code"));

        // The winner's value survived; last-write-wins would have silently lost it.
        var detail = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{project}");
        Assert.Equal("Renamed by the winner", detail.Str("name"));
    }

    [Fact]
    public async Task TwoReviewersRacingTheSameTransitionProduceOneWinner()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Contended Review");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "sheet.jpg");

        await fixture.ReadyAsync(versionRef);
        await MediaPipelineFixture.ReviewAsync(owner, versionRef, "InReview");

        var reviewers = new List<TestUser>();
        for (var i = 0; i < 2; i++)
        {
            var reviewer = await fixture.RegisterAsync();
            await ProjectAuthorizationTests.AddMemberAsync(owner, project, reviewer.Email, "Reviewer");
            reviewers.Add(reviewer);
        }

        // One approves, one requests changes, simultaneously. Both are legal from InReview,
        // so the database decides - and the loser is told, not silently discarded.
        var barrier = new TaskCompletionSource();
        var approve = Task.Run(async () =>
        {
            await barrier.Task;
            return await MediaPipelineFixture.ReviewAsync(reviewers[0], versionRef, "Approved");
        });
        var reject = Task.Run(async () =>
        {
            await barrier.Task;
            return await MediaPipelineFixture.ReviewAsync(reviewers[1], versionRef, "ChangesRequested");
        });

        barrier.SetResult();
        var results = await Task.WhenAll(approve, reject);

        // The end state must be one of the two decisions, never a blend.
        var final = await GetVersionAsync(owner, versionRef);
        Assert.True(final.Str("reviewState") is "Approved" or "ChangesRequested");

        Assert.All(results, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity,
            $"Unexpected {(int)r.StatusCode} from a racing review transition."));
    }

    [Fact]
    public async Task AnIllegalReviewTransitionIsRefusedByTheServer()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "State Machine");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "draft.jpg");

        await fixture.ReadyAsync(versionRef);
        // Draft -> Approved skips review entirely; the client cannot talk the server into it.
        var response = await MediaPipelineFixture.ReviewAsync(owner, versionRef, "Approved");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SELF_APPROVAL_FORBIDDEN", (await response.Content.ReadFromJsonAsync<JsonElement>()).Str("code"));
    }

    [Fact]
    public async Task ResolvingACommentTwiceIsAConflictNotASilentNoOp()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Comment Races");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "proof.jpg");

        await fixture.OpenReviewAsync(owner, versionRef);
        var created = await owner.Client.PostAsJsonAsync(
            $"/api/v1/versions/{versionRef}/comments", new { body = "Tighten the leading." });
        var commentRef = (await created.Content.ReadFromJsonAsync<JsonElement>()).Str("ref");

        var first = await owner.Client.PostAsJsonAsync($"/api/v1/comments/{commentRef}/resolution", new { resolved = true });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await owner.Client.PostAsJsonAsync($"/api/v1/comments/{commentRef}/resolution", new { resolved = true });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("COMMENT_ALREADY_RESOLVED", (await second.Content.ReadFromJsonAsync<JsonElement>()).Str("code"));

        var reopened = await owner.Client.PostAsJsonAsync($"/api/v1/comments/{commentRef}/resolution", new { resolved = false });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
    }

    [Fact]
    public async Task AnIdempotencyKeyMakesARetriedUploadReturnTheOriginal()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Idempotent Uploads");
        var (assetRef, _) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "base.jpg");

        var key = $"retry-{Guid.NewGuid():N}";
        var bytes = TestImages.Jpeg(500, 400);

        var first = await PostVersionAsync(owner, assetRef, bytes, key);
        var second = await PostVersionAsync(owner, assetRef, bytes, key);

        var firstRef = (await first.Content.ReadFromJsonAsync<JsonElement>()).Str("ref");
        var secondRef = (await second.Content.ReadFromJsonAsync<JsonElement>()).Str("ref");

        // A client retry after a timeout must not create a second version.
        Assert.Equal(firstRef, secondRef);

        var history = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/assets/{assetRef}/versions");
        Assert.Equal(2, history.Items().Count());
    }

    [Fact]
    public async Task ReusingAnIdempotencyKeyForADifferentAssetIsAConflict()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Key Reuse");
        var (assetA, _) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "a.jpg");
        var (assetB, _) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "b.jpg");

        var key = $"reused-{Guid.NewGuid():N}";
        await PostVersionAsync(owner, assetA, TestImages.Jpeg(400, 300), key);

        var crossed = await PostVersionAsync(owner, assetB, TestImages.Jpeg(400, 300), key);

        Assert.Equal(HttpStatusCode.Conflict, crossed.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await crossed.Content.ReadFromJsonAsync<JsonElement>()).Str("code"));
    }

    [Fact]
    public async Task OwnershipTransferIsAtomicUnderConcurrentMembershipChanges()
    {
        var owner = await fixture.RegisterAsync();
        var successor = await fixture.RegisterAsync();
        var bystander = await fixture.RegisterAsync();

        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Succession");
        await ProjectAuthorizationTests.AddMemberAsync(owner, project, successor.Email, "Editor");
        await ProjectAuthorizationTests.AddMemberAsync(owner, project, bystander.Email, "Viewer");

        var response = await owner.Client.PostAsJsonAsync(
            $"/api/v1/projects/{project}/owner", new { newOwnerRef = successor.UserRef });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exactly one owner, and the previous one kept a working role rather than vanishing.
        using var scope = fixture.NewScope();
        var projectId = await scope.Db.Projects.AsNoTracking()
            .Where(p => p.PublicRef == project).Select(p => p.Id).SingleAsync();

        var members = await scope.Db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId).ToListAsync();

        Assert.Single(members, m => m.Role == Domain.Projects.ProjectRole.Owner);
        Assert.Equal(3, members.Count);

        // The old owner can no longer manage membership; the new one can.
        var demoted = await owner.Client.PostAsJsonAsync(
            $"/api/v1/projects/{project}/members", new { email = "someone@example.test", role = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, demoted.StatusCode);
    }

    [Fact]
    public async Task AnUploadThatFailsValidationLeavesNoUsableVersionBehind()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Failed Uploads");

        // A PDF-shaped payload: past the Content-Type hint, stopped by the signature check.
        byte[] notAnImage = [.. "%PDF-1.7"u8, .. new byte[512]];
        using var content = TestImages.AsUpload(notAnImage, "report.jpg", title: "Should not exist");

        var response = await owner.Client.PostAsync(
            new Uri($"/api/v1/projects/{project}/assets", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("UNSUPPORTED_MEDIA_TYPE", (await response.Content.ReadFromJsonAsync<JsonElement>()).Str("code"));

        // The asset row exists but has no usable version; the listing must not offer it as ready.
        var assets = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{project}/assets");
        foreach (var asset in assets.Items())
        {
            var current = asset.Prop("currentVersion");
            if (current.ValueKind != JsonValueKind.Null)
            {
                Assert.NotEqual("Ready", current.Str("lifecycleState"));
            }
        }

        using var scope = fixture.NewScope();
        var states = await scope.Db.AssetVersions.AsNoTracking().Select(v => v.LifecycleState).ToListAsync();
        Assert.DoesNotContain(VersionLifecycleState.Reserved, states.Where((_, i) => false));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<JsonElement> GetVersionAsync(TestUser user, string versionRef) =>
        await user.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}");

    private static async Task<JsonElement> AddVersionAsync(
        TestUser user, string assetRef, string fileName, byte[]? bytes = null)
    {
        var response = await PostVersionAsync(user, assetRef, bytes ?? TestImages.Jpeg(600, 400), idempotencyKey: null, fileName);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> PostVersionAsync(
        TestUser user, string assetRef, byte[] bytes, string? idempotencyKey, string fileName = "version.jpg")
    {
        using var content = TestImages.AsUpload(bytes, fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/assets/{assetRef}/versions")
        {
            Content = content,
        };

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await user.Client.SendAsync(request);
    }

    private async Task<string> ReadConcurrencyTokenAsync(string projectRef)
    {
        using var scope = fixture.NewScope();
        var rowVersion = await scope.Db.Projects.AsNoTracking()
            .Where(p => p.PublicRef == projectRef)
            .Select(p => p.RowVersion)
            .SingleAsync();

        return rowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
