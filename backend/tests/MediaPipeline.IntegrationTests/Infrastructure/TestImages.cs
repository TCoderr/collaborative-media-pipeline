using System.Net.Http.Headers;
using System.Text.Json;
using SkiaSharp;

namespace MediaPipeline.IntegrationTests.Infrastructure;

/// <summary>
/// Generates genuine encoded images so the worker performs real decoding and re-encoding.
/// A byte array that merely starts with a JPEG signature would pass the upload sniffer and
/// then prove nothing about the processing pipeline.
/// </summary>
public static class TestImages
{
    public static byte[] Jpeg(int width = 1200, int height = 800, SKColor? colour = null) =>
        Encode(width, height, SKEncodedImageFormat.Jpeg, colour);

    public static byte[] Png(int width = 640, int height = 480, SKColor? colour = null) =>
        Encode(width, height, SKEncodedImageFormat.Png, colour);

    /// <summary>
    /// A file whose signature says JPEG but whose body is noise. It gets past the upload
    /// sniffer and fails inside the decoder - exactly the permanent failure the retry
    /// classifier must recognise and refuse to retry.
    /// </summary>
    public static byte[] CorruptJpeg(int size = 4096)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        return bytes;
    }

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, SKColor? colour)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(colour ?? new SKColor(18, 18, 20));

        // A little structure so the derivative is not a flat colour that compresses to
        // almost nothing - the tests assert on plausible derivative sizes.
        using var paint = new SKPaint { Color = new SKColor(198, 106, 58), IsAntialias = true };
        for (var i = 0; i < 12; i++)
        {
            canvas.DrawCircle(width * (i + 1) / 13f, height / 2f, MathF.Min(width, height) / 8f, paint);
        }

        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>Builds the multipart body the upload endpoints expect.</summary>
    public static MultipartFormDataContent AsUpload(byte[] bytes, string fileName, string? title = null, string? contentType = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? (bytes.Length > 0 && bytes[0] == 0x89 ? "image/png" : "image/jpeg"));
        content.Add(file, "file", fileName);

        if (title is not null)
        {
            content.Add(new StringContent(title), "title");
        }

        return content;
    }
}

/// <summary>Small helpers that keep the assertions in the tests readable.</summary>
public static class JsonHelpers
{
    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static int Number(this JsonElement element, string property) => element.GetProperty(property).GetInt32();

    public static long Sequence(this JsonElement element, string property) => element.GetProperty(property).GetInt64();

    public static JsonElement Prop(this JsonElement element, string property) => element.GetProperty(property);

    public static IEnumerable<JsonElement> Items(this JsonElement page) => page.GetProperty("items").EnumerateArray();
}
