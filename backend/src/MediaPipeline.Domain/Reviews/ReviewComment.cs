using MediaPipeline.Domain.Common;

namespace MediaPipeline.Domain.Reviews;

/// <summary>
/// A plain-text review note attached to one asset version. Deliberately not rich text:
/// storing markup would put an XSS sink in the collaboration path for no product gain.
/// </summary>
public sealed class ReviewComment
{
    public const int MaxBodyLength = 4000;

    private ReviewComment() { }

    public Guid Id { get; private set; }
    public string PublicRef { get; private set; } = string.Empty;
    public Guid AssetVersionId { get; private set; }
    public Guid? ParentCommentId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid AuthorUserId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public Guid? ResolvedByUserId { get; private set; }
    public long StateSequence { get; private set; }
    public uint RowVersion { get; private set; }

    public bool IsResolved => ResolvedAt is not null;

    public static ReviewComment Create(
        Guid assetVersionId,
        Guid projectId,
        Guid authorUserId,
        string body,
        DateTimeOffset now, Guid? parentCommentId = null)
    {
        var text = Normalise(body);
        if (text.Length == 0)
        {
            throw new DomainException("EMPTY_COMMENT", "A comment cannot be empty.");
        }

        if (text.Length > MaxBodyLength)
        {
            throw new DomainException("COMMENT_TOO_LONG", $"A comment must be at most {MaxBodyLength} characters.");
        }

        return new ReviewComment
        {
            Id = Guid.CreateVersion7(),
            ParentCommentId = parentCommentId,
            PublicRef = Common.PublicRef.New(Common.PublicRef.CommentPrefix),
            AssetVersionId = assetVersionId,
            ProjectId = projectId,
            AuthorUserId = authorUserId,
            Body = text,
            CreatedAt = now,
            UpdatedAt = now,
            StateSequence = 1,
        };
    }

    public void Resolve(Guid resolvedByUserId, DateTimeOffset now)
    {
        if (IsResolved)
        {
            throw new InvalidStateTransitionException(nameof(ReviewComment), "Resolved", "Resolved");
        }

        ResolvedAt = now;
        ResolvedByUserId = resolvedByUserId;
        UpdatedAt = now;
        StateSequence++;
    }

    public void Reopen(DateTimeOffset now)
    {
        if (!IsResolved)
        {
            throw new InvalidStateTransitionException(nameof(ReviewComment), "Open", "Open");
        }

        ResolvedAt = null;
        ResolvedByUserId = null;
        UpdatedAt = now;
        StateSequence++;
    }

    /// <summary>
    /// Strips control characters (except tab and newline) and normalises line endings,
    /// so stored text is exactly what will be rendered as text content.
    /// </summary>
    private static string Normalise(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var normalised = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var buffer = new System.Text.StringBuilder(normalised.Length);
        foreach (var c in normalised)
        {
            if (!char.IsControl(c) || c is '\n' or '\t')
            {
                buffer.Append(c);
            }
        }

        return buffer.ToString().Trim();
    }
}
