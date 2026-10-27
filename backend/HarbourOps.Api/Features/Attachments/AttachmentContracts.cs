using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;

namespace HarbourOps.Api.Features.Attachments;

public sealed record AttachmentResponse(
    Guid Id,
    string FileName,
    string MediaType,
    long SizeBytes,
    int Width,
    int Height,
    int PreviewWidth,
    int PreviewHeight,
    PersonSummary UploadedBy,
    UserRole UploaderRole,
    DateTimeOffset UploadedAt,
    string PreviewUrl,
    string ContentUrl);

public sealed record AttachmentLimits(
    int MaxPerWorkOrder, long MaxFileSizeBytes, int MaxDimension, long MaxPixels, IReadOnlyList<string> AcceptedTypes);

/// <summary>
/// The photos on a work order, oldest first, plus whether the caller may add another right now
/// (and, if not, why) so the interface can explain instead of failing after the upload.
/// </summary>
public sealed record AttachmentList(
    IReadOnlyList<AttachmentResponse> Items, AttachmentLimits Limits, bool CanUpload, string? CannotUploadReason);

public sealed record UploadedFile(byte[] Content, string? SubmittedFileName);

public static partial class DisplayFileName
{
    private const int MaxStemLength = 100;

    /// <summary>
    /// A safe name for display and for the Content-Disposition header. Directory parts, control characters,
    /// invisible formatting characters (including right-to-left overrides that disguise extensions), quotes,
    /// and path punctuation are removed. The extension is always .jpg because the stored file is a re-encoded JPEG.
    /// The name is never used to build a storage path.
    /// </summary>
    public static string Sanitize(string? submitted)
    {
        var name = (submitted ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];

        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (char.IsControl(c) || category is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
                || "\"<>:*?|;".Contains(c))
            {
                continue;
            }

            cleaned.Append(c);
        }

        var stem = Path.GetFileNameWithoutExtension(Whitespace().Replace(cleaned.ToString(), " ").Trim()).Trim().Trim('.');
        if (stem.Length == 0)
        {
            stem = "photo";
        }

        return (stem.Length > MaxStemLength ? stem[..MaxStemLength].TrimEnd() : stem) + ".jpg";
    }

    /// <summary>ASCII-only fallback for the plain <c>filename=</c> parameter (the UTF-8 <c>filename*=</c> carries the real name).</summary>
    public static string AsciiFallback(string name) =>
        new(name.Select(c => c is >= ' ' and <= '~' ? c : '_').ToArray());

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
