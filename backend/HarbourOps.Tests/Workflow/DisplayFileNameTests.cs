using HarbourOps.Api.Features.Attachments;

namespace HarbourOps.Tests.Workflow;

public sealed class DisplayFileNameTests
{
    [Theory]
    [InlineData("kitchen leak.png", "kitchen leak.jpg")]                         // always .jpg: the stored file is a JPEG
    [InlineData("../../etc/passwd", "passwd.jpg")]                               // no directories
    [InlineData("C:\\Users\\riley\\IMG_0042.JPG", "IMG_0042.jpg")]
    [InlineData("evil\"\r\nSet-Cookie: pwned=1.png", "evilSet-Cookie pwned=1.jpg")] // no quotes, CR/LF, or colons
    [InlineData("invoice\u202Egpj.exe", "invoicegpj.jpg")]                       // right-to-left override removed
    [InlineData("   ", "photo.jpg")]
    [InlineData(null, "photo.jpg")]
    [InlineData(".hidden", "photo.jpg")]
    public void Names_are_safe_for_display_and_headers(string? submitted, string expected)
    {
        Assert.Equal(expected, DisplayFileName.Sanitize(submitted));
    }

    [Fact]
    public void Long_names_are_shortened()
    {
        var name = DisplayFileName.Sanitize(new string('a', 300) + ".png");

        Assert.True(name.Length <= Attachment.FileNameMaxLength);
        Assert.EndsWith(".jpg", name);
    }

    [Fact]
    public void Ascii_fallback_replaces_non_ascii_characters()
    {
        Assert.Equal("caf_ d_j_.jpg", DisplayFileName.AsciiFallback("café déjà.jpg"));
    }
}
