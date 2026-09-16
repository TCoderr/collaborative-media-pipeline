using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaPipeline.IntegrationTests.Infrastructure;
using Xunit;

namespace MediaPipeline.IntegrationTests;

/// <summary>
/// Horizontal access control against the real database.
/// <para>
/// The threat these cover is the cheapest one to attempt and the most damaging to miss: a
/// signed-in user guessing or being handed another project's identifiers. Every resource
/// type gets its own attempt, because a single forgotten membership join is all it takes.
/// </para>
/// </summary>
[Collection(MediaPipelineTestGroup.Name)]
public sealed class ProjectAuthorizationTests(MediaPipelineFixture fixture)
{
    [Fact]
    public async Task AnOutsiderCannotReadAnotherUsersProject()
    {
        var owner = await fixture.RegisterAsync();
        var outsider = await fixture.RegisterAsync();

        var project = await CreateProjectAsync(owner, "Aster & Verdigris");

        var response = await outsider.Client.GetAsync(new Uri($"/api/v1/projects/{project}", UriKind.Relative));

        // 404 rather than 403: confirming existence would let an outsider enumerate projects.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PROJECT_NOT_FOUND", body.Str("code"));
    }

    [Fact]
    public async Task AnOutsiderCannotListAnotherProjectsAssets()
    {
        var owner = await fixture.RegisterAsync();
        var outsider = await fixture.RegisterAsync();
        var project = await CreateProjectAsync(owner, "Field Notes");

        var response = await outsider.Client.GetAsync(new Uri($"/api/v1/projects/{project}/assets", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnOutsiderCannotReadAnAssetAVersionOrItsMedia()
    {
        var owner = await fixture.RegisterAsync();
        var outsider = await fixture.RegisterAsync();

        var project = await CreateProjectAsync(owner, "Nocturne");
        var (assetRef, versionRef) = await UploadAssetAsync(owner, project, "plate-01.jpg");

        foreach (var path in new[]
        {
            $"/api/v1/assets/{assetRef}",
            $"/api/v1/assets/{assetRef}/versions",
            $"/api/v1/versions/{versionRef}",
            $"/api/v1/versions/{versionRef}/content",
            $"/api/v1/versions/{versionRef}/content?derivative=thumbnail",
            $"/api/v1/versions/{versionRef}/comments",
        })
        {
            var response = await outsider.Client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"{path} returned {(int)response.StatusCode}; a non-member must never reach it.");
        }
    }

    [Fact]
    public async Task AnOutsiderCannotWriteToAnotherProject()
    {
        var owner = await fixture.RegisterAsync();
        var outsider = await fixture.RegisterAsync();

        var project = await CreateProjectAsync(owner, "Nocturne");
        var (assetRef, versionRef) = await UploadAssetAsync(owner, project, "plate-01.jpg");

        var comment = await outsider.Client.PostAsJsonAsync(
            $"/api/v1/versions/{versionRef}/comments", new { body = "I should not be able to say this." });
        Assert.Equal(HttpStatusCode.NotFound, comment.StatusCode);

        var review = await outsider.Client.PostAsJsonAsync(
            $"/api/v1/versions/{versionRef}/review", new { reviewState = "InReview" });
        Assert.Equal(HttpStatusCode.NotFound, review.StatusCode);

        using var upload = TestImages.AsUpload(TestImages.Jpeg(320, 240), "sneaky.jpg");
        var version = await outsider.Client.PostAsync(new Uri($"/api/v1/assets/{assetRef}/versions", UriKind.Relative), upload);
        Assert.Equal(HttpStatusCode.NotFound, version.StatusCode);
    }

    [Fact]
    public async Task AViewerCanReadButNotWrite()
    {
        var owner = await fixture.RegisterAsync();
        var viewer = await fixture.RegisterAsync();

        var project = await CreateProjectAsync(owner, "Longform Review");
        await AddMemberAsync(owner, project, viewer.Email, "Viewer");

        var (assetRef, versionRef) = await UploadAssetAsync(owner, project, "spread.jpg");

        var read = await viewer.Client.GetAsync(new Uri($"/api/v1/assets/{assetRef}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // Reading is allowed; every mutation is refused with 403, since membership is proven.
        var comment = await viewer.Client.PostAsJsonAsync(
            $"/api/v1/versions/{versionRef}/comments", new { body = "Nice." });
        Assert.Equal(HttpStatusCode.Forbidden, comment.StatusCode);
        Assert.Equal("INSUFFICIENT_PROJECT_ROLE", (await comment.Content.ReadFromJsonAsync<JsonElement>()).Str("code"));

        using var upload = TestImages.AsUpload(TestImages.Jpeg(320, 240), "v2.jpg");
        var version = await viewer.Client.PostAsync(new Uri($"/api/v1/assets/{assetRef}/versions", UriKind.Relative), upload);
        Assert.Equal(HttpStatusCode.Forbidden, version.StatusCode);
    }

    [Fact]
    public async Task AReviewerCanCommentAndReviewButNotUpload()
    {
        var owner = await fixture.RegisterAsync();
        var reviewer = await fixture.RegisterAsync();

        var project = await CreateProjectAsync(owner, "Meridian");
        await AddMemberAsync(owner, project, reviewer.Email, "Reviewer");
        var (assetRef, versionRef) = await UploadAssetAsync(owner, project, "specimen.jpg");

        await fixture.OpenReviewAsync(owner, versionRef);
        var comment = await reviewer.Client.PostAsJsonAsync(
            $"/api/v1/versions/{versionRef}/comments", new { body = "The counters need air." });
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);

        var review = await MediaPipelineFixture.ReviewAsync(reviewer, versionRef, "InReview");
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);

        using var upload = TestImages.AsUpload(TestImages.Jpeg(320, 240), "v2.jpg");
        var version = await reviewer.Client.PostAsync(new Uri($"/api/v1/assets/{assetRef}/versions", UriKind.Relative), upload);
        Assert.Equal(HttpStatusCode.Forbidden, version.StatusCode);
    }

    [Fact]
    public async Task OnlyTheOwnerCanManageMembership()
    {
        var owner = await fixture.RegisterAsync();
        var editor = await fixture.RegisterAsync();
        var stranger = await fixture.RegisterAsync();

        var project = await CreateProjectAsync(owner, "Håndværk");
        await AddMemberAsync(owner, project, editor.Email, "Editor");

        var attempt = await editor.Client.PostAsJsonAsync(
            $"/api/v1/projects/{project}/members", new { email = stranger.Email, role = "Viewer" });

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequestsAreRejectedEverywhere()
    {
        var client = fixture.NewClient();

        foreach (var path in new[]
        {
            "/api/v1/projects",
            "/api/v1/assets/ast_0000000000000000000000",
            "/api/v1/versions/ver_0000000000000000000000",
            "/api/v1/operations/pipeline",
        })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task MalformedReferencesAreNotFoundRatherThanServerErrors()
    {
        var user = await fixture.RegisterAsync();

        foreach (var reference in new[]
        {
            "not-a-ref",
            "prj_'; DROP TABLE projects;--",
            "prj_" + new string('a', 200),
            "../../etc/passwd",
        })
        {
            var response = await user.Client.GetAsync(
                new Uri($"/api/v1/projects/{Uri.EscapeDataString(reference)}", UriKind.Relative));

            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                $"'{reference}' produced {(int)response.StatusCode}; malformed input must never reach a 500.");
        }
    }

    [Fact]
    public async Task AnArchivedProjectIsReadableButFrozen()
    {
        var owner = await fixture.RegisterAsync();
        var project = await CreateProjectAsync(owner, "Retired Work");
        var (assetRef, _) = await UploadAssetAsync(owner, project, "old.jpg");

        var archive = await owner.Client.PostAsJsonAsync($"/api/v1/projects/{project}/status", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var read = await owner.Client.GetAsync(new Uri($"/api/v1/projects/{project}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var upload = TestImages.AsUpload(TestImages.Jpeg(320, 240), "new.jpg");
        var write = await owner.Client.PostAsync(new Uri($"/api/v1/assets/{assetRef}/versions", UriKind.Relative), upload);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, write.StatusCode);
        Assert.Equal("PROJECT_ARCHIVED", (await write.Content.ReadFromJsonAsync<JsonElement>()).Str("code"));
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    internal static async Task<string> CreateProjectAsync(TestUser user, string name)
    {
        var response = await user.Client.PostAsJsonAsync("/api/v1/projects", new { name, description = "" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Str("ref");
    }

    internal static async Task AddMemberAsync(TestUser owner, string projectRef, string email, string role)
    {
        var response = await owner.Client.PostAsJsonAsync($"/api/v1/projects/{projectRef}/members", new { email, role });
        response.EnsureSuccessStatusCode();
    }

    internal static async Task<(string AssetRef, string VersionRef)> UploadAssetAsync(
        TestUser user, string projectRef, string fileName, byte[]? bytes = null)
    {
        using var content = TestImages.AsUpload(bytes ?? TestImages.Jpeg(), fileName, title: fileName);

        var response = await user.Client.PostAsync(
            new Uri($"/api/v1/projects/{projectRef}/assets", UriKind.Relative), content);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var assetRef = body.Str("ref");
        var versionRef = body.Prop("versions").EnumerateArray().First().Str("ref");
        return (assetRef, versionRef);
    }
}
