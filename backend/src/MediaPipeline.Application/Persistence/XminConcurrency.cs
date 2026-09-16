using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediaPipeline.Application.Persistence;

/// <summary>
/// Maps a <c>uint</c> concurrency property onto PostgreSQL's built-in <c>xmin</c> system
/// column.
/// <para>
/// <c>xmin</c> holds the id of the transaction that last wrote the row, so PostgreSQL
/// maintains it for free on every update. That is strictly better than a hand-maintained
/// version column here: there is no column to create, no trigger to keep correct, and no way
/// for a write path that forgets to bump it to silently disable optimistic concurrency.
/// </para>
/// <para>
/// The trade-off is that the token is a transaction id rather than a per-row counter, so it
/// is opaque and not comparable across rows. Clients only ever echo it back verbatim in
/// <c>If-Match</c>, which is all the protocol needs.
/// </para>
/// </summary>
public static class XminConcurrency
{
    public static PropertyBuilder<uint> MapToXmin(this PropertyBuilder<uint> builder) =>
        builder
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
}
