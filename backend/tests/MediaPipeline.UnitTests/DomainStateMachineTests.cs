using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Common;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Domain.Projects;
using MediaPipeline.Domain.Reviews;
using Xunit;

namespace MediaPipeline.UnitTests;

/// <summary>
/// The domain's guarantees, tested without a database. These are the rules the API relies on
/// being unbreakable, so each illegal transition gets an explicit test rather than being
/// covered incidentally by a happy-path integration test.
/// </summary>
public sealed class AssetVersionLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static AssetVersion Reserved() => AssetVersion.Reserve(
        Guid.CreateVersion7(), Guid.CreateVersion7(), 1,
        id => StorageKeys.Source(Guid.CreateVersion7(), Guid.CreateVersion7(), id),
        "photo.jpg", Guid.CreateVersion7(), Now);

    [Fact]
    public void ReserveStartsInReservedWithAServerGeneratedKey()
    {
        var version = Reserved();

        Assert.Equal(VersionLifecycleState.Reserved, version.LifecycleState);
        Assert.Equal(ProcessingState.Pending, version.ProcessingState);
        Assert.Equal(ReviewState.Draft, version.ReviewState);
        Assert.StartsWith("quarantine/", version.SourceObjectKey, StringComparison.Ordinal);
        Assert.DoesNotContain("photo.jpg", version.SourceObjectKey, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkStoredIsOnlyLegalOnce()
    {
        var version = Reserved();
        version.MarkStored("image/jpeg", 1024, new string('a', 64), Now);

        Assert.Equal(VersionLifecycleState.Stored, version.LifecycleState);

        // Re-storing would silently rewrite an immutable version's checksum.
        var ex = Assert.Throws<InvalidStateTransitionException>(
            () => version.MarkStored("image/png", 2048, new string('b', 64), Now));
        Assert.Equal("INVALID_STATE_TRANSITION", ex.Code);
    }

    [Fact]
    public void MarkStoredRejectsAnEmptyBody()
    {
        var version = Reserved();
        var ex = Assert.Throws<DomainException>(() => version.MarkStored("image/jpeg", 0, "x", Now));
        Assert.Equal("EMPTY_UPLOAD", ex.Code);
    }

    [Fact]
    public void AStoredVersionCannotBeMarkedUploadFailed()
    {
        var version = Reserved();
        version.MarkStored("image/jpeg", 10, "x", Now);

        Assert.Throws<InvalidStateTransitionException>(() => version.MarkUploadFailed(Now));
    }

    [Fact]
    public void FailedProcessingNeverBecomesReady()
    {
        var version = Reserved();
        version.MarkStored("image/jpeg", 10, "x", Now);
        version.MarkQueued(Now);
        version.MarkProcessing(Now);
        version.MarkProcessingFailed(exhausted: true, Now);

        // Failure preserves the source privately, without advertising it as ready.
        Assert.Equal(ProcessingState.DeadLettered, version.ProcessingState);
        Assert.Equal(VersionLifecycleState.Stored, version.LifecycleState);
    }

    [Fact]
    public void ProcessingSuccessIsReplayable()
    {
        var version = Reserved();
        version.MarkStored("image/jpeg", 10, "x", Now);
        version.MarkQueued(Now);
        version.MarkProcessing(Now);
        version.MarkProcessingSucceeded(800, 600, Now);

        // At-least-once delivery means the same outcome legitimately arrives twice.
        version.MarkProcessingSucceeded(800, 600, Now);

        Assert.Equal(ProcessingState.Succeeded, version.ProcessingState);
        Assert.Equal(800, version.Width);
    }

    [Fact]
    public void StateSequenceIncreasesOnEveryChange()
    {
        var version = Reserved();
        var initial = version.StateSequence;

        version.MarkStored("image/jpeg", 10, "x", Now);
        var afterStore = version.StateSequence;
        version.MarkQueued(Now);

        Assert.True(afterStore > initial);
        Assert.True(version.StateSequence > afterStore);
    }

    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\System32\\evil.jpg", "evil.jpg")]
    [InlineData("/var/www/shell.php", "shell.php")]
    [InlineData("   ", "untitled")]
    [InlineData("normal name.jpg", "normal name.jpg")]
    public void FilenamesAreReducedToALeafName(string input, string expected)
    {
        Assert.Equal(expected, AssetVersion.SanitiseFileName(input));
    }

    [Fact]
    public void ControlCharactersAreStrippedFromFilenames()
    {
        var sanitised = AssetVersion.SanitiseFileName("a\u0000b\u001fc.jpg");
        Assert.DoesNotContain('\u0000', sanitised);
        Assert.DoesNotContain('\u001f', sanitised);
    }
}

public sealed class ReviewStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static AssetVersion Stored()
    {
        var version = AssetVersion.Reserve(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 1,
            id => StorageKeys.Source(Guid.CreateVersion7(), Guid.CreateVersion7(), id),
            "photo.jpg", Guid.CreateVersion7(), Now);
        version.MarkStored("image/jpeg", 100, "x", Now);
        version.MarkQueued(Now);
        version.MarkProcessing(Now);
        version.MarkProcessingSucceeded(32, 32, Now);
        return version;
    }

    [Theory]
    [InlineData(ReviewState.Approved)]
    [InlineData(ReviewState.ChangesRequested)]
    public void DraftCannotJumpStraightToADecision(ReviewState target)
    {
        var version = Stored();
        Assert.Throws<InvalidStateTransitionException>(() => { if (target == ReviewState.Approved) version.Approve(Guid.NewGuid(), Now); else version.ChangeReviewState(target, Now); });
    }

    [Fact]
    public void TheFullReviewCycleIsAllowed()
    {
        var version = Stored();

        version.ChangeReviewState(ReviewState.InReview, Now);
        version.ChangeReviewState(ReviewState.ChangesRequested, Now);
        version.ChangeReviewState(ReviewState.InReview, Now);
        version.Approve(Guid.NewGuid(), Now);

        Assert.Equal(ReviewState.Approved, version.ReviewState);
    }

    [Fact]
    public void AnUnstoredVersionIsNotReviewable()
    {
        var version = AssetVersion.Reserve(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 1,
            id => StorageKeys.Source(Guid.CreateVersion7(), Guid.CreateVersion7(), id),
            "photo.jpg", Guid.CreateVersion7(), Now);

        var ex = Assert.Throws<DomainException>(() => version.ChangeReviewState(ReviewState.InReview, Now));
        Assert.Equal("VERSION_NOT_REVIEWABLE", ex.Code);
    }

    [Fact]
    public void SettingTheSameStateIsANoOpRatherThanAnError()
    {
        var version = Stored();
        version.ChangeReviewState(ReviewState.Draft, Now);
        Assert.Equal(ReviewState.Draft, version.ReviewState);
    }
}

public sealed class ProjectMembershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewProjectAlwaysHasExactlyOneOwner()
    {
        var ownerId = Guid.CreateVersion7();
        var project = Project.Create("Nocturne", "A film", ownerId, Now);

        var owners = project.Members.Where(m => m.Role == ProjectRole.Owner).ToList();
        Assert.Single(owners);
        Assert.Equal(ownerId, owners[0].UserId);
    }

    [Fact]
    public void TheOwnerCannotBeRemovedOrDemoted()
    {
        var ownerId = Guid.CreateVersion7();
        var project = Project.Create("Nocturne", null, ownerId, Now);

        Assert.Equal("CANNOT_REMOVE_OWNER", Assert.Throws<DomainException>(
            () => project.RemoveMember(ownerId, Now)).Code);

        Assert.Equal("CANNOT_DEMOTE_OWNER", Assert.Throws<DomainException>(
            () => project.ChangeMemberRole(ownerId, ProjectRole.Viewer, Now)).Code);
    }

    [Fact]
    public void OwnershipTransferLeavesExactlyOneOwner()
    {
        var ownerId = Guid.CreateVersion7();
        var editorId = Guid.CreateVersion7();
        var project = Project.Create("Nocturne", null, ownerId, Now);
        project.AddMember(editorId, ProjectRole.Editor, Now);

        project.TransferOwnership(editorId, Now);

        Assert.Equal(editorId, project.OwnerUserId);
        Assert.Single(project.Members, m => m.Role == ProjectRole.Owner);
        Assert.Equal(ProjectRole.Editor, project.RoleOf(ownerId));
    }

    [Fact]
    public void OwnershipCannotBeTransferredToANonMember()
    {
        var project = Project.Create("Nocturne", null, Guid.CreateVersion7(), Now);
        Assert.Equal("MEMBER_NOT_FOUND", Assert.Throws<DomainException>(
            () => project.TransferOwnership(Guid.CreateVersion7(), Now)).Code);
    }

    [Fact]
    public void TheOwnerRoleCannotBeHandedOutDirectly()
    {
        var project = Project.Create("Nocturne", null, Guid.CreateVersion7(), Now);
        Assert.Equal("OWNER_ROLE_NOT_ASSIGNABLE", Assert.Throws<DomainException>(
            () => project.AddMember(Guid.CreateVersion7(), ProjectRole.Owner, Now)).Code);
    }

    [Fact]
    public void ArchivedProjectsRejectNewWork()
    {
        var project = Project.Create("Nocturne", null, Guid.CreateVersion7(), Now);
        project.ChangeStatus(ProjectStatus.Archived, Now);
        Assert.False(project.AcceptsNewWork);
    }
}

public sealed class ProjectAuthorizationTests
{
    [Theory]
    [InlineData(ProjectRole.Viewer, ProjectCapability.ViewProject, true)]
    [InlineData(ProjectRole.Viewer, ProjectCapability.Comment, false)]
    [InlineData(ProjectRole.Viewer, ProjectCapability.UploadAsset, false)]
    [InlineData(ProjectRole.Reviewer, ProjectCapability.Comment, true)]
    [InlineData(ProjectRole.Reviewer, ProjectCapability.ChangeReviewState, true)]
    [InlineData(ProjectRole.Reviewer, ProjectCapability.CreateVersion, false)]
    [InlineData(ProjectRole.Editor, ProjectCapability.CreateVersion, true)]
    [InlineData(ProjectRole.Editor, ProjectCapability.RetryProcessing, true)]
    [InlineData(ProjectRole.Editor, ProjectCapability.ManageMembers, false)]
    [InlineData(ProjectRole.Owner, ProjectCapability.ManageMembers, true)]
    [InlineData(ProjectRole.Owner, ProjectCapability.ManageProject, true)]
    public void CapabilitiesMatchTheDocumentedRoleMatrix(
        ProjectRole role, ProjectCapability capability, bool expected)
    {
        Assert.Equal(expected, ProjectAuthorization.Allows(role, capability));
    }

    [Fact]
    public void EveryCapabilityHasAnExplicitDecision()
    {
        // A capability added without a switch arm would silently fall through to `false`
        // for Owner too, which is the signal that the matrix was not updated.
        foreach (var capability in Enum.GetValues<ProjectCapability>())
        {
            Assert.True(
                ProjectAuthorization.Allows(ProjectRole.Owner, capability),
                $"Owner has no decision for {capability}; add it to ProjectAuthorization.");
        }
    }
}

public sealed class ProcessingJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ProcessingJob Job() => ProcessingJob.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    [Fact]
    public void ASucceededJobRefusesASecondClaim()
    {
        var job = Job();
        Assert.True(job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now));
        job.MarkSucceeded(Now);

        // A duplicate delivery must be acknowledged, not reprocessed.
        Assert.False(job.TryClaim("worker-2", TimeSpan.FromMinutes(5), Now));
    }

    [Fact]
    public void ALiveClaimBlocksACompetingWorker()
    {
        var job = Job();
        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now);

        var ex = Assert.Throws<DomainException>(
            () => job.TryClaim("worker-2", TimeSpan.FromMinutes(5), Now.AddSeconds(30)));
        Assert.Equal("JOB_ALREADY_CLAIMED", ex.Code);
    }

    [Fact]
    public void AnExpiredClaimIsReclaimable()
    {
        var job = Job();
        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now);

        // worker-1 died; its lease lapsed, so the redelivery can proceed.
        Assert.True(job.TryClaim("worker-2", TimeSpan.FromMinutes(5), Now.AddMinutes(6)));
        Assert.Equal(2, job.AttemptCount);
    }

    [Fact]
    public void PermanentFailuresAreNeverRetried()
    {
        var job = Job();
        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now);

        var retryable = job.MarkFailed(ProcessingFailureKind.Permanent, "MEDIA_UNREADABLE", "Corrupt file.", Now);

        Assert.False(retryable);
        Assert.Equal(ProcessingState.DeadLettered, job.State);
        Assert.True(job.IsTerminal);
    }

    [Fact]
    public void TransientFailuresRetryUntilTheBudgetIsSpent()
    {
        var job = ProcessingJob.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Now, maxAttempts: 3);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now);
            Assert.True(job.MarkFailed(ProcessingFailureKind.Transient, "IO", "Storage blip.", Now));
        }

        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now);
        Assert.False(job.MarkFailed(ProcessingFailureKind.Transient, "IO", "Storage blip.", Now));
        Assert.Equal(ProcessingState.DeadLettered, job.State);
    }

    [Fact]
    public void FailureMessagesAreLengthBounded()
    {
        var job = Job();
        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now);
        job.MarkFailed(ProcessingFailureKind.Permanent, "X", new string('e', 5000), Now);

        Assert.NotNull(job.FailureMessage);
        Assert.True(job.FailureMessage!.Length <= ProcessingJob.MaxErrorMessageLength);
    }

    [Fact]
    public void RetryPreservesThePreviousFailureAndGrantsAFreshBudget()
    {
        var job = Job();
        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now);
        job.MarkFailed(ProcessingFailureKind.Transient, "STORAGE_TEMPORARY", "Storage unavailable.", Now);

        job.PrepareRetry(Now.AddMinutes(1));

        Assert.Equal(ProcessingState.Queued, job.State);
        Assert.Equal("STORAGE_TEMPORARY", job.FailureCode);
        Assert.True(job.MaxAttempts > job.AttemptCount);
    }

    [Fact]
    public void ASucceededJobCannotBeRetried()
    {
        var job = Job();
        job.TryClaim("worker-1", TimeSpan.FromMinutes(5), Now);
        job.MarkSucceeded(Now);

        Assert.Equal("JOB_ALREADY_SUCCEEDED", Assert.Throws<DomainException>(() => job.PrepareRetry(Now)).Code);
    }
}

public sealed class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static OutboxMessage Message() => OutboxMessage.Create(
        Guid.CreateVersion7(), "media.processing.requested", 1, "{}",
        Guid.CreateVersion7(), "ver_test", null, Now);

    [Fact]
    public void RetryBackoffGrowsAndIsCapped()
    {
        var message = Message();
        var delays = new List<double>();

        for (var i = 0; i < 8; i++)
        {
            var before = Now.AddSeconds(i);
            message.MarkAttemptFailed("broker down", before, maxAttempts: 20, TimeSpan.FromSeconds(2));
            delays.Add((message.NextAttemptAt - before).TotalSeconds);
        }

        Assert.True(delays[1] > delays[0], "Backoff must grow between attempts.");
        Assert.All(delays, d => Assert.True(d <= 300, $"Backoff {d}s exceeded the 300s cap."));
    }

    [Fact]
    public void TheAttemptBudgetIsFinite()
    {
        var message = Message();
        for (var i = 0; i < 3; i++)
        {
            message.MarkAttemptFailed("broker down", Now, maxAttempts: 3, TimeSpan.FromSeconds(1));
        }

        Assert.Equal(OutboxStatus.Abandoned, message.Status);
    }

    [Fact]
    public void PublishingClearsTheLeaseAndTheLastError()
    {
        var message = Message();
        message.MarkAttemptFailed("blip", Now, 10, TimeSpan.FromSeconds(1));
        message.Lease("publisher-1", TimeSpan.FromSeconds(30), Now);

        message.MarkPublished(Now);

        Assert.Equal(OutboxStatus.Published, message.Status);
        Assert.Null(message.LockedBy);
        Assert.Null(message.LastError);
    }
}

public sealed class CommentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ReviewComment Comment(string body) =>
        ReviewComment.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), body, Now);

    [Fact]
    public void MarkupIsStoredVerbatimAsTextNotInterpreted()
    {
        // Comments are plain text end to end; the API returns JSON and the client sets
        // textContent, so the payload never becomes markup.
        const string Payload = "<script>alert('xss')</script>";
        Assert.Equal(Payload, Comment(Payload).Body);
    }

    [Fact]
    public void EmptyAndWhitespaceCommentsAreRejected()
    {
        Assert.Equal("EMPTY_COMMENT", Assert.Throws<DomainException>(() => Comment("   \n\t  ")).Code);
    }

    [Fact]
    public void OversizedCommentsAreRejected()
    {
        Assert.Equal("COMMENT_TOO_LONG",
            Assert.Throws<DomainException>(() => Comment(new string('a', ReviewComment.MaxBodyLength + 1))).Code);
    }

    [Fact]
    public void ControlCharactersAreStrippedButNewlinesSurvive()
    {
        const char Newline = (char)10;
        const char Tab = (char)9;
        const char Bell = (char)7;

        var comment = Comment($"line one{Newline}line two{Tab}tabbed{Bell}bell");

        // Layout characters survive because a reviewer's line breaks are meaningful;
        // every other control character is stripped before it is ever stored.
        Assert.Contains(comment.Body, c => c == Newline);
        Assert.Contains(comment.Body, c => c == Tab);
        Assert.DoesNotContain(comment.Body, c => char.IsControl(c) && c != Newline && c != Tab);
    }

    [Fact]
    public void ResolveAndReopenAreEachOnlyLegalOnce()
    {
        var comment = Comment("Needs a tighter crop.");
        var resolver = Guid.CreateVersion7();

        comment.Resolve(resolver, Now);
        Assert.True(comment.IsResolved);
        Assert.Throws<InvalidStateTransitionException>(() => comment.Resolve(resolver, Now));

        comment.Reopen(Now);
        Assert.False(comment.IsResolved);
        Assert.Throws<InvalidStateTransitionException>(() => comment.Reopen(Now));
    }
}

public sealed class StorageKeyTests
{
    [Fact]
    public void GeneratedKeysAreBuiltOnlyFromServerIdentifiers()
    {
        var key = StorageKeys.Source(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        StorageKeys.EnsureSafe(key);
        Assert.Matches(@"^quarantine/[0-9a-f]{32}/[0-9a-f]{32}/[0-9a-f]{32}/source$", key);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("projects/../../secret")]
    [InlineData("/absolute/path")]
    [InlineData("projects\\windows\\path")]
    [InlineData("C:/drive/letter")]
    [InlineData("projects/a b/space")]
    [InlineData("projects/a%00/null")]
    public void UnsafeKeysAreRejected(string key)
    {
        Assert.Equal("INVALID_OBJECT_KEY", Assert.Throws<DomainException>(() => StorageKeys.EnsureSafe(key)).Code);
    }

    [Fact]
    public void DerivativeKeysAreDeterministicSoReprocessingOverwrites()
    {
        var projectId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        var versionId = Guid.CreateVersion7();

        var first = StorageKeys.Derivative(projectId, assetId, versionId, DerivativeKind.Thumbnail, "webp");
        var second = StorageKeys.Derivative(projectId, assetId, versionId, DerivativeKind.Thumbnail, "webp");

        Assert.Equal(first, second);
    }
}

public sealed class PublicRefTests
{
    [Fact]
    public void GeneratedRefsCarryTheirPrefixAndParseBack()
    {
        var reference = PublicRef.New(PublicRef.ProjectPrefix);

        Assert.StartsWith("prj_", reference, StringComparison.Ordinal);
        Assert.True(PublicRef.TryParse(reference, PublicRef.ProjectPrefix, out _));
    }

    [Fact]
    public void ARefForOneEntityTypeNeverParsesAsAnother()
    {
        var assetRef = PublicRef.New(PublicRef.AssetPrefix);
        Assert.False(PublicRef.TryParse(assetRef, PublicRef.ProjectPrefix, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("prj_")]
    [InlineData("prj")]
    [InlineData("prj_UPPERCASE")]
    [InlineData("prj_has spaces")]
    [InlineData("prj_'; DROP TABLE projects;--")]
    public void MalformedRefsAreRejectedBeforeTheyReachAQuery(string? value)
    {
        Assert.False(PublicRef.TryParse(value, PublicRef.ProjectPrefix, out _));
    }

    [Fact]
    public void RefsAreUniqueAcrossManyGenerations()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 20_000; i++)
        {
            Assert.True(seen.Add(PublicRef.New(PublicRef.AssetPrefix)), "Duplicate public ref generated.");
        }
    }
}
