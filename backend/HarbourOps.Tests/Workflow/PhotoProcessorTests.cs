using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Attachments.Processing;
using HarbourOps.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace HarbourOps.Tests.Workflow;

/// <summary>Image validation and normalisation, without HTTP or a database.</summary>
public sealed class PhotoProcessorTests
{
    private static PhotoProcessor Processor(Action<AttachmentOptions>? configure = null)
    {
        var options = new AttachmentOptions();
        configure?.Invoke(options);
        return new PhotoProcessor(Options.Create(options));
    }

    [Fact]
    public void Jpeg_and_png_are_accepted_and_stored_as_jpeg()
    {
        using var processor = Processor();

        var jpeg = processor.Process(TestImages.Jpeg(640, 480));
        var png = processor.Process(TestImages.Png(300, 200));

        Assert.Equal(PhotoProcessor.JpegMediaType, jpeg.Value!.SourceMediaType);
        Assert.Equal(PhotoProcessor.PngMediaType, png.Value!.SourceMediaType);
        Assert.Equal(PhotoProcessor.JpegMediaType, PhotoProcessor.Sniff(png.Value.Full.Bytes));
        Assert.Equal((640, 480), (jpeg.Value.Full.Width, jpeg.Value.Full.Height));
    }

    [Theory]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("RIFF\u0000\u0000\u0000\u0000WEBPVP8 ")]
    public void Content_that_is_not_jpeg_or_png_is_unsupported_whatever_its_name(string content)
    {
        using var processor = Processor();

        var result = processor.Process(System.Text.Encoding.Latin1.GetBytes(content));

        Assert.Equal(FailureKind.UnsupportedMediaType, result.Failure!.Kind);
    }

    [Fact]
    public void Truncated_and_garbage_images_are_rejected()
    {
        using var processor = Processor();
        byte[] fakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[200]];

        Assert.Equal("invalid_image", processor.Process(TestImages.Truncated(TestImages.Jpeg(800, 600))).Failure!.Code);
        Assert.Equal("invalid_image", processor.Process(fakeJpeg).Failure!.Code);
    }

    [Fact]
    public void Declared_dimensions_are_checked_before_any_pixels_are_decoded()
    {
        using var processor = Processor();

        // A ~70-byte PNG that claims to be 30,000 × 30,000 (900 megapixels) is refused from its header alone.
        var result = processor.Process(TestImages.PngDeclaring(30_000, 30_000));

        Assert.Equal("image_too_large", result.Failure!.Code);
    }

    [Theory]
    [InlineData(401, 10)]   // longer than MaxDimension
    [InlineData(300, 300)]  // 90,000 pixels > MaxPixels
    public void Dimension_and_pixel_limits_are_enforced(int width, int height)
    {
        using var processor = Processor(o => { o.MaxDimension = 400; o.MaxPixels = 80_000; });

        Assert.Equal("image_too_large", processor.Process(TestImages.Png(width, height)).Failure!.Code);
    }

    [Fact]
    public void Exif_orientation_is_applied_and_all_metadata_is_removed()
    {
        using var processor = Processor();
        // 40 × 20, left half red, right half blue, tagged "rotate 90° clockwise to display" (orientation 6).
        var upload = TestImages.WithExifAndComment(TestImages.Jpeg(40, 20), orientation: 6);
        Assert.True(TestImages.ContainsAscii(upload, "Exif"));

        var photo = processor.Process(upload).Value!;

        Assert.Equal((20, 40), (photo.Full.Width, photo.Full.Height)); // upright: now portrait
        using var stored = TestImages.Decode(photo.Full.Bytes);
        Assert.True(TestImages.IsReddish(stored.GetPixel(10, 5)), "The left (red) half should now be at the top.");
        Assert.True(TestImages.IsBluish(stored.GetPixel(10, 34)), "The right (blue) half should now be at the bottom.");
        foreach (var bytes in new[] { photo.Full.Bytes, photo.Preview.Bytes })
        {
            Assert.False(TestImages.ContainsAscii(bytes, "Exif"));
            Assert.False(TestImages.ContainsAscii(bytes, TestImages.SecretComment));
        }
    }

    [Fact]
    public void Large_photos_are_scaled_down_and_previews_are_small()
    {
        using var processor = Processor();

        var photo = processor.Process(TestImages.Jpeg(3000, 1000)).Value!;

        Assert.Equal((2560, 853), (photo.Full.Width, photo.Full.Height));
        Assert.Equal((480, 160), (photo.Preview.Width, photo.Preview.Height));
        Assert.True(photo.Preview.Bytes.Length < photo.Full.Bytes.Length);
    }

    [Fact]
    public void Transparent_png_areas_become_white_not_black()
    {
        using var processor = Processor();

        var photo = processor.Process(TestImages.Png(100, 50, transparent: true)).Value!;

        using var stored = TestImages.Decode(photo.Full.Bytes);
        var pixel = stored.GetPixel(10, 25); // the transparent left half
        Assert.True(pixel.Red > 240 && pixel.Green > 240 && pixel.Blue > 240, $"Expected white, got {pixel}");
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 40, 20)]
    [InlineData(SKEncodedOrigin.BottomRight, 40, 20)]
    [InlineData(SKEncodedOrigin.RightTop, 20, 40)]
    [InlineData(SKEncodedOrigin.LeftBottom, 20, 40)]
    public void Every_orientation_produces_the_right_shape(SKEncodedOrigin origin, int width, int height)
    {
        using var source = new SKBitmap(40, 20);
        using var upright = PhotoProcessor.ApplyOrientation(source, origin);

        Assert.Equal((width, height), (upright.Width, upright.Height));
    }
}
