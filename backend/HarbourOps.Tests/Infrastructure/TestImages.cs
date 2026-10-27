using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>Generates real JPEG and PNG files (and deliberately broken ones) for upload tests.</summary>
public static class TestImages
{
    public const string SecretComment = "harbour-secret-location-note";

    /// <summary>A JPEG whose left half is <paramref name="left"/> and right half is <paramref name="right"/>.</summary>
    public static byte[] Jpeg(int width, int height, SKColor? left = null, SKColor? right = null) =>
        Encode(width, height, left ?? SKColors.Red, right ?? SKColors.Blue, SKEncodedImageFormat.Jpeg, transparent: false);

    public static byte[] Png(int width, int height, bool transparent = false) =>
        Encode(width, height, SKColors.Red, SKColors.Blue, SKEncodedImageFormat.Png, transparent);

    /// <summary>
    /// Adds an EXIF block (orientation tag plus a GPS IFD) and a JPEG comment, as phones do. The upload must
    /// come back upright with none of this metadata.
    /// </summary>
    public static byte[] WithExifAndComment(byte[] jpeg, ushort orientation)
    {
        // Little-endian TIFF: IFD0 with Orientation (0x0112) and a GPS IFD pointer (0x8825); GPS IFD with GPSLatitudeRef.
        var tiff = new byte[56];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 2);
        WriteEntry(tiff, 10, 0x0112, 3, 1, orientation);
        WriteEntry(tiff, 22, 0x8825, 4, 1, 38);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(38), 1);
        WriteEntry(tiff, 40, 0x0001, 2, 2, 'N');

        var app1 = new List<byte> { 0xFF, 0xE1 };
        app1.AddRange(BigEndian((ushort)(2 + 6 + tiff.Length)));
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(tiff);

        var comment = Encoding.ASCII.GetBytes(SecretComment);
        var com = new List<byte> { 0xFF, 0xFE };
        com.AddRange(BigEndian((ushort)(2 + comment.Length)));
        com.AddRange(comment);

        return [.. jpeg[..2], .. app1, .. com, .. jpeg[2..]];
    }

    /// <summary>A valid 1×1 PNG whose header claims <paramref name="width"/> × <paramref name="height"/> ("decompression bomb" shape).</summary>
    public static byte[] PngDeclaring(int width, int height)
    {
        var png = Png(1, 1);
        // IHDR data starts at byte 16: width (4), height (4), then 5 more bytes; CRC covers "IHDR" + 13 data bytes.
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32(png.AsSpan(12, 17)));
        return png;
    }

    public static byte[] Truncated(byte[] image) => image[..(image.Length / 2)];

    public static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes);

    public static bool IsReddish(SKColor c) => c.Red > 180 && c.Blue < 90 && c.Green < 90;

    public static bool IsBluish(SKColor c) => c.Blue > 180 && c.Red < 90 && c.Green < 90;

    public static bool ContainsAscii(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;

    private static byte[] Encode(int width, int height, SKColor left, SKColor right, SKEncodedImageFormat format, bool transparent)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(transparent ? SKColors.Transparent : left);
            using var paint = new SKPaint { Color = right };
            canvas.DrawRect(width / 2f, 0, width / 2f, height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    private static void WriteEntry(byte[] tiff, int offset, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(offset + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset + 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset + 8), value);
    }

    private static byte[] BigEndian(ushort value) => [(byte)(value >> 8), (byte)value];

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
