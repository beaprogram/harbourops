using HarbourOps.Api.Common;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace HarbourOps.Api.Features.Attachments.Processing;

public sealed record EncodedImage(byte[] Bytes, int Width, int Height);

/// <summary>A validated photo, re-encoded as JPEG: the full-size image and a small gallery preview.</summary>
public sealed record ProcessedPhoto(string SourceMediaType, EncodedImage Full, EncodedImage Preview);

/// <summary>
/// Turns untrusted upload bytes into safe, normalised JPEGs using SkiaSharp (Google's Skia, the same codecs
/// Chrome uses). The original file is never stored:
///  1. Identify the format from the bytes themselves (magic numbers), ignoring the file name and Content-Type.
///  2. Read only the header and reject oversized dimensions before decoding any pixels.
///  3. Decode (JPEGs are decoded at a reduced scale when they are larger than needed, which bounds memory).
///  4. Resize, apply the EXIF orientation so the stored image is upright, and flatten onto white in sRGB.
///  5. Encode fresh JPEGs. The encoder writes no EXIF, GPS, comments, or other metadata from the original.
/// </summary>
public sealed class PhotoProcessor(IOptions<AttachmentOptions> options) : IDisposable
{
    public const string JpegMediaType = "image/jpeg";
    public const string PngMediaType = "image/png";

    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly SKSamplingOptions DownscaleSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly AttachmentOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(options.Value.MaxConcurrentProcessing);

    public async Task<Result<ProcessedPhoto>> ProcessAsync(byte[] content, CancellationToken ct)
    {
        // Decoding is CPU- and memory-heavy, so only a few photos are processed at once.
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return Process(content);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal Result<ProcessedPhoto> Process(byte[] content)
    {
        var sourceMediaType = Sniff(content);
        if (sourceMediaType is null)
        {
            return Unsupported();
        }

        using var data = SKData.CreateCopy(content);
        using var codec = SKCodec.Create(data);
        var expectedFormat = sourceMediaType == JpegMediaType ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        if (codec is null || codec.EncodedFormat != expectedFormat)
        {
            return new Failure(FailureKind.Validation, "This file isn't a readable JPEG or PNG image.",
                new Dictionary<string, string[]> { ["file"] = ["This file isn't a readable JPEG or PNG image."] }, "invalid_image");
        }

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0 || Math.Max(width, height) > _options.MaxDimension || (long)width * height > _options.MaxPixels)
        {
            var message = $"Photos can be at most {_options.MaxDimension:N0} pixels on a side and " +
                          $"{_options.MaxPixels / 1_000_000.0:0.#} megapixels. This one is {width:N0} × {height:N0}.";
            return new Failure(FailureKind.Validation, message,
                new Dictionary<string, string[]> { ["file"] = [message] }, "image_too_large");
        }

        using var decoded = Decode(codec, sourceMediaType);
        if (decoded is null)
        {
            return new Failure(FailureKind.Validation, "This image is damaged or incomplete.",
                new Dictionary<string, string[]> { ["file"] = ["This image is damaged or incomplete."] }, "invalid_image");
        }

        // Resize first (orientation doesn't change the long edge), then rotate the smaller bitmap.
        using var fitted = ResizeToFit(decoded, _options.StoredMaxEdge);
        using var upright = ApplyOrientation(fitted, codec.EncodedOrigin);
        using var preview = ResizeToFit(upright, _options.PreviewMaxEdge);

        return new ProcessedPhoto(sourceMediaType, Encode(upright), Encode(preview));
    }

    /// <summary>Identifies JPEG and PNG by their leading bytes. Everything else (GIF, WebP, SVG, HTML, …) is unsupported.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> content) =>
        content.StartsWith(JpegMagic) ? JpegMediaType : content.StartsWith(PngMagic) ? PngMediaType : null;

    private SKBitmap? Decode(SKCodec codec, string sourceMediaType)
    {
        var size = new SKSizeI(codec.Info.Width, codec.Info.Height);
        var longEdge = Math.Max(size.Width, size.Height);
        if (sourceMediaType == JpegMediaType && longEdge > _options.StoredMaxEdge)
        {
            // JPEG can decode directly at 1/2, 1/4, or 1/8 scale. Use the smallest that still covers the target.
            var scaled = codec.GetScaledDimensions((float)_options.StoredMaxEdge / longEdge);
            if (Math.Max(scaled.Width, scaled.Height) >= _options.StoredMaxEdge)
            {
                size = scaled;
            }
        }

        // Decoding into sRGB converts colours from any embedded ICC profile, so no profile is needed afterwards.
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result == SKCodecResult.Success)
        {
            return bitmap;
        }

        bitmap.Dispose(); // IncompleteInput (truncated file) and every other result are rejected
        return null;
    }

    private static SKBitmap ResizeToFit(SKBitmap source, int maxEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxEdge)
        {
            return source.Copy();
        }

        var scale = (double)maxEdge / longEdge;
        var info = source.Info.WithSize(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
        return source.Resize(info, DownscaleSampling);
    }

    /// <summary>
    /// Draws the pixels upright according to the EXIF orientation (the stored JPEG has no EXIF, so
    /// it must already be upright), on a white background so transparent PNG areas don't turn black.
    /// </summary>
    internal static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        // x' = ScaleX·x + SkewX·y + TransX ; y' = SkewY·x + ScaleY·y + TransY
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),       // mirrored horizontally
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),   // rotated 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),     // mirrored vertically
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),         // transposed
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),       // rotate 90° clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),   // transverse
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),     // rotate 90° counter-clockwise
            _ => SKMatrix.Identity,
        };

        var info = new SKImageInfo(
            swapsAxes ? source.Height : source.Width,
            swapsAxes ? source.Width : source.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var result = new SKBitmap(info);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), null);
        return result;
    }

    private EncodedImage Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, _options.JpegQuality);
        return new EncodedImage(encoded.ToArray(), bitmap.Width, bitmap.Height);
    }

    private static Failure Unsupported() =>
        new(FailureKind.UnsupportedMediaType, "Only JPEG and PNG photos are supported.", Code: "unsupported_image");

    public void Dispose() => _gate.Dispose();
}
