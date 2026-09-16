using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Common;
using MediaPipeline.Domain.Processing;
using Xunit;
namespace MediaPipeline.UnitTests;
public sealed class StrengthenedInvariantsTests
{
    [Fact]
    public void DuplicateClaimFromTheSameWorkerCannotStartAnotherAttempt()
    {
        var now = DateTimeOffset.UtcNow;
        var job = ProcessingJob.Create(Guid.NewGuid(), Guid.NewGuid(), now);
        job.TryClaim("worker", TimeSpan.FromMinutes(1), now);
        Assert.Throws<DomainException>(() => job.TryClaim("worker", TimeSpan.FromMinutes(1), now));
        Assert.Equal(1, job.AttemptCount);
    }
    [Fact]
    public void PermanentFailureCannotBeManuallyReplayed()
    {
        var now = DateTimeOffset.UtcNow;
        var job = ProcessingJob.Create(Guid.NewGuid(), Guid.NewGuid(), now);
        job.TryClaim("worker", TimeSpan.FromMinutes(1), now);
        job.MarkFailed(ProcessingFailureKind.Permanent, "CORRUPT", "Invalid media.", now);
        Assert.Throws<DomainException>(() => job.PrepareRetry(now));
        Assert.False(job.TryClaim("other", TimeSpan.FromMinutes(1), now));
    }
    [Fact]
    public void QueuedJobCannotReceiveExtraManualRetryBudget()
    {
        var job = ProcessingJob.Create(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() => job.PrepareRetry(DateTimeOffset.UtcNow));
        Assert.Equal(ProcessingJob.DefaultMaxAttempts, job.MaxAttempts);
    }
}
