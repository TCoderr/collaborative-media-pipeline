using System.Security.Cryptography;
using System.Text;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Contracts;
using MediaPipeline.Application.Media;
using Xunit;

namespace MediaPipeline.UnitTests;

/// <summary>
/// The upload edge: what the server will accept, and how it decides. These run without any
/// infrastructure because the decisions are pure - which is the point of keeping them there.
/// </summary>
public sealed class MediaSignatureTests
{
    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF"u8];
    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static byte[] WebP() => [.. "RIFF"u8, 0x20, 0, 0, 0, .. "WEBP"u8, .. "VP8 "u8];

    [Fact]
    public void RealSignaturesAreRecognised()
    {
        Assert.Equal("image/jpeg", MediaSignatures.Detect(Jpeg()).ContentType);
        Assert.Equal("image/png", MediaSignatures.Detect(Png()).ContentType);
        Assert.Equal("image/webp", MediaSignatures.Detect(WebP()).ContentType);
    }

    [Fact]
    public void ADeclaredContentTypeCannotSmuggleADisallowedFile()
    {
        // The bytes are a Windows executable; the client may claim whatever it likes.
        byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];

        var ex = Assert.Throws<MediaPipelineException>(() => MediaSignatures.Detect(executable));
        Assert.Equal("UNSUPPORTED_MEDIA_TYPE", ex.Code);
    }

    [Theory]
    [InlineData(new byte[] { 0x3C, 0x3F, 0x70, 0x68, 0x70 })]                 // <?php
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 })]                        // <svg - scriptable
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 })]                        // zip / office
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]            // gif - outside policy
    public void FilesOutsideTheAcceptedSetAreRejected(byte[] head)
    {
        Assert.Throws<MediaPipelineException>(() => MediaSignatures.Detect(head));
    }

    [Fact]
    public void ARiffContainerThatIsNotWebpIsRejected()
    {
        // RIFF....WAVE - the four-byte discriminator is what decides, not the container.
        byte[] wave = [.. "RIFF"u8, 0x20, 0, 0, 0, .. "WAVE"u8, .. "fmt "u8];
        Assert.Throws<MediaPipelineException>(() => MediaSignatures.Detect(wave));
    }
}

public sealed class HashingUploadStreamTests
{
    [Fact]
    public async Task TheDigestCoversTheSniffWindowAndTheRemainder()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 5000));
        var expected = Convert.ToHexStringLower(SHA256.HashData(payload));

        // Mirror the real path: the first bytes were already consumed to sniff the type.
        var prefix = payload.AsMemory(0, MediaSignatures.SniffLength);
        using var remainder = new MemoryStream(payload, MediaSignatures.SniffLength, payload.Length - MediaSignatures.SniffLength);
        await using var stream = new HashingUploadStream(remainder, prefix, maxBytes: 1024 * 1024);

        using var sink = new MemoryStream();
        await stream.CopyToAsync(sink);

        Assert.Equal(payload.Length, stream.BytesRead);
        Assert.Equal(expected, stream.Digest);
        Assert.Equal(payload, sink.ToArray());
    }

    [Fact]
    public async Task TheSizeCapTripsDuringTheCopyNotAfterIt()
    {
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        using var source = new MemoryStream(payload);
        await using var stream = new HashingUploadStream(source, ReadOnlyMemory<byte>.Empty, maxBytes: 8 * 1024);

        using var sink = new MemoryStream();
        var ex = await Assert.ThrowsAsync<MediaPipelineException>(() => stream.CopyToAsync(sink));

        Assert.Equal("UPLOAD_TOO_LARGE", ex.Code);

        // The transfer was cut short rather than being fully accepted and then rejected.
        Assert.True(sink.Length < payload.Length);
    }

    [Fact]
    public async Task AnEmptyBodyIsRejectedAtTheSniffStage()
    {
        using var empty = new MemoryStream();
        var ex = await Assert.ThrowsAsync<MediaPipelineException>(
            () => UploadSniffer.SniffAsync(empty, CancellationToken.None));

        Assert.Equal("EMPTY_UPLOAD", ex.Code);
    }

    [Fact]
    public async Task SniffingLeavesTheRestOfTheStreamReadable()
    {
        byte[] payload = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[100]];
        using var source = new MemoryStream(payload);

        var (media, prefix) = await UploadSniffer.SniffAsync(source, CancellationToken.None);

        Assert.Equal("image/jpeg", media.ContentType);
        Assert.Equal(MediaSignatures.SniffLength, prefix.Length);

        await using var hashing = new HashingUploadStream(source, prefix, maxBytes: 1024 * 1024);
        using var sink = new MemoryStream();
        await hashing.CopyToAsync(sink);

        Assert.Equal(payload.Length, sink.Length);
    }
}

public sealed class CursorTests
{
    [Fact]
    public void ACursorRoundTrips()
    {
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var id = Guid.CreateVersion7();

        var decoded = Cursor.Decode(Cursor.Encode(createdAt, id));

        Assert.NotNull(decoded);
        Assert.Equal(createdAt.UtcTicks, decoded!.Value.CreatedAt.UtcTicks);
        Assert.Equal(id, decoded.Value.Id);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("aGVsbG8")]
    [InlineData("MTIzNDo=")]
    public void ATamperedCursorIsAValidationErrorNotACrash(string cursor)
    {
        Assert.Equal("INVALID_CURSOR", Assert.Throws<MediaPipelineException>(() => Cursor.Decode(cursor)).Code);
    }

    [Fact]
    public void AnAbsentCursorMeansTheFirstPage()
    {
        Assert.Null(Cursor.Decode(null));
        Assert.Null(Cursor.Decode("   "));
    }

    [Fact]
    public void PageLimitsAreClampedToTheAllowedRange()
    {
        // Oversized asks are capped so a client cannot turn a page into a full table scan.
        Assert.Equal(PageRequest.MaxLimit, new PageRequest { Limit = 10_000 }.Normalised().Limit);

        // Non-positive means "unspecified", which is the default page rather than an error.
        Assert.Equal(PageRequest.DefaultLimit, new PageRequest { Limit = 0 }.Normalised().Limit);
        Assert.Equal(PageRequest.DefaultLimit, new PageRequest { Limit = -5 }.Normalised().Limit);

        Assert.Equal(7, new PageRequest { Limit = 7 }.Normalised().Limit);
    }
}

public sealed class ConcurrencyTokenTests
{
    [Fact]
    public void AMatchingTokenIsAccepted()
    {
        ConcurrencyToken.Enforce(ConcurrencyToken.Format(4242), 4242);
    }

    [Fact]
    public void AQuotedEtagStyleTokenIsAccepted()
    {
        ConcurrencyToken.Enforce("\"4242\"", 4242);
    }

    [Fact]
    public void AnAbsentTokenSkipsTheCheck()
    {
        // Endpoints opt in by sending If-Match; blind writes stay possible where they are safe.
        ConcurrencyToken.Enforce(null, 4242);
        ConcurrencyToken.Enforce("", 4242);
    }

    [Fact]
    public void AStaleTokenIsAConflict()
    {
        Assert.Equal("CONCURRENT_MODIFICATION",
            Assert.Throws<MediaPipelineException>(() => ConcurrencyToken.Enforce("4241", 4242)).Code);
    }

    [Fact]
    public void AGarbageTokenIsAConflictRatherThanASilentPass()
    {
        Assert.Equal("CONCURRENT_MODIFICATION",
            Assert.Throws<MediaPipelineException>(() => ConcurrencyToken.Enforce("not-a-number", 4242)).Code);
    }
}

public sealed class PasswordHasherTests
{
    // Exercised through the port the application depends on, not the concrete class.
    private readonly IdentityPasswordHasher _hasher = new IdentityPasswordHasher();

    [Fact]
    public void ACorrectPasswordVerifies()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.True(_hasher.Verify(hash, "correct horse battery staple").Verified);
    }

    [Fact]
    public void AWrongPasswordDoesNotVerify()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.False(_hasher.Verify(hash, "Correct horse battery staple").Verified);
    }

    [Fact]
    public void TheSamePasswordHashesDifferentlyEveryTime()
    {
        // Per-password salt: identical passwords must not produce identical hashes.
        Assert.NotEqual(_hasher.Hash("same password here"), _hasher.Hash("same password here"));
    }

    [Fact]
    public void ACorruptHashFailsClosed()
    {
        Assert.False(_hasher.Verify("not-a-real-hash", "anything").Verified);
    }
}
