using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Demo.Sandbox;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HarbourOps.Api.Features.Attachments;

public static class AttachmentEndpoints
{
    /// <summary>Room for the multipart envelope (boundaries and part headers) on top of the file itself.</summary>
    private const long MultipartOverheadBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/work-orders/{workOrderId:guid}/attachments")
            .WithTags("Attachments")
            .RequireAuthorization();

        group.MapGet("/", async (Guid workOrderId, CurrentUser user, AttachmentService service, CancellationToken ct) =>
                (await service.ListAsync(user, workOrderId, ct)).ToOk())
            .WithName("ListAttachments")
            .WithSummary("Photo metadata for a work order, oldest first, with limits and whether the caller can add one.")
            .Produces<AttachmentList>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", UploadAsync)
            .WithName("UploadAttachment")
            .WithSummary("Upload one JPEG or PNG photo as multipart/form-data (field \"file\"). Requires the antiforgery header.")
            // No .Accepts<IFormFile>(): that metadata makes routing skip this endpoint for other content types,
            // and the request would fall through to the /api 404. The handler answers 415 itself instead.
            .Produces<AttachmentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{attachmentId:guid}/preview", (Guid workOrderId, Guid attachmentId, HttpContext http, CurrentUser user, AttachmentService service, CancellationToken ct) =>
                ServeAsync(http, service.OpenAsync(user, workOrderId, attachmentId, AttachmentRendition.Preview, ct), download: false))
            .WithName("GetAttachmentPreview")
            .WithSummary("The small preview image. Checked against current work-order access on every request.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{attachmentId:guid}/content", (Guid workOrderId, Guid attachmentId, bool? download, HttpContext http, CurrentUser user, AttachmentService service, CancellationToken ct) =>
                ServeAsync(http, service.OpenAsync(user, workOrderId, attachmentId, AttachmentRendition.Full, ct), download == true))
            .WithName("GetAttachmentContent")
            .WithSummary("The full-size image (inline, or as a download with ?download=true).")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> UploadAsync(
        Guid workOrderId, HttpRequest request, CurrentUser user, AttachmentService service,
        UploadAdmission admission, DemoQuotas demoQuotas, IOptions<AttachmentOptions> options, CancellationToken ct)
    {
        // Reject early, before reading the body, when the photo could never be accepted.
        if (await service.PrecheckUploadAsync(user, workOrderId, ct) is { } refused)
        {
            return refused.ToProblem();
        }

        // Held until the response is produced, so buffered bodies plus decodes stay within a fixed bound.
        using var lease = admission.TryAcquire();
        if (lease is null)
        {
            request.HttpContext.Response.Headers.RetryAfter = "5";
            return new Failure(FailureKind.Unavailable,
                "Several photos are being processed right now. Try again in a few seconds.", Code: "uploads_busy").ToProblem();
        }

        // Demo personas: every attempt counts (before the body is read), so a sandbox can't keep the decoder busy.
        if (await demoQuotas.TryUseAsync(user, DemoQuota.Upload, ct) is { } limited)
        {
            return limited.ToProblem();
        }

        // The global request limit is 256 KB; raise it for this endpoint only, just enough for one photo.
        var maxFileSize = options.Value.MaxFileSizeBytes;
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxFileSize + MultipartOverheadBytes;
        }

        var file = await ReadSinglePhotoAsync(request, maxFileSize, ct);
        if (file.Failure is { } invalid)
        {
            return invalid.ToProblem();
        }

        var result = await service.UploadAsync(user, workOrderId, file.Value!, ct);
        return result.IsSuccess
            ? TypedResults.Created(result.Value!.ContentUrl, result.Value)
            : result.Failure!.ToProblem();
    }

    /// <summary>
    /// Reads the multipart body by hand instead of binding IFormFile, so exactly one part named "file" is
    /// accepted, its size is enforced while streaming (never buffered past the limit), and no temporary
    /// files are created on disk. The declared Content-Type and file name of the part are not trusted;
    /// the bytes are checked by <see cref="Processing.PhotoProcessor"/>.
    /// </summary>
    internal static async Task<Result<UploadedFile>> ReadSinglePhotoAsync(HttpRequest request, long maxFileSize, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return new Failure(FailureKind.UnsupportedMediaType, "Send the photo as multipart/form-data.", Code: "multipart_required");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 70)
        {
            return Failure.Validation("file", "The upload is malformed. Choose the photo again.");
        }

        var tooLarge = new Failure(FailureKind.PayloadTooLarge,
            $"Photos must be {maxFileSize / (1024.0 * 1024.0):0.#} MiB or smaller.", Code: "file_too_large");
        var reader = new MultipartReader(boundary, request.Body)
        {
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024,
            BodyLengthLimit = maxFileSize + 1, // any part (file or not) bigger than a photo is rejected
        };

        try
        {
            for (var parts = 0; parts < 4; parts++)
            {
                var section = await reader.ReadNextSectionAsync(ct);
                if (section is null)
                {
                    break;
                }

                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.IsFileDisposition()
                    || !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, "file", StringComparison.Ordinal))
                {
                    await section.Body.CopyToAsync(Stream.Null, ct); // skip other fields
                    continue;
                }

                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await section.Body.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > maxFileSize)
                    {
                        return tooLarge;
                    }

                    buffer.Write(chunk, 0, read);
                }

                if (buffer.Length == 0)
                {
                    return Failure.Validation("file", "The chosen file is empty.");
                }

                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                    ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                return new UploadedFile(buffer.ToArray(), fileName);
            }
        }
        catch (InvalidDataException exception) when (exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        {
            return tooLarge;
        }
        catch (InvalidDataException)
        {
            return Failure.Validation("file", "The upload is malformed. Choose the photo again.");
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return tooLarge;
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        {
            // The body ended before its closing boundary (or was otherwise cut short): a client error, not a 500.
            // A real disconnect cancels the request token instead, and that exception is left to propagate.
            return Failure.Validation("file", "The upload is malformed. Choose the photo again.");
        }

        return Failure.Validation("file", "Choose a photo to upload.");
    }

    private static async Task<IResult> ServeAsync(HttpContext http, Task<Result<AttachmentContent>> opening, bool download)
    {
        var opened = await opening;
        if (opened.Failure is { } failure)
        {
            return failure.ToProblem();
        }

        var content = opened.Value!;
        var headers = http.Response.Headers;
        // Never cached by browsers or proxies: access is re-checked on every request, so a user who loses
        // access (for example a reassigned technician) can't keep reading photos from a cache.
        headers.CacheControl = "private, no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        // The header value is built by ContentDispositionHeaderValue from an already-sanitised name, so
        // quotes and line breaks can't break out of it.
        headers.ContentDisposition = new ContentDispositionHeaderValue(download ? "attachment" : "inline")
        {
            FileName = DisplayFileName.AsciiFallback(content.FileName),
            FileNameStar = content.FileName,
        }.ToString();

        // The stream is disposed by the result after it has been copied to the response.
        return TypedResults.Stream(content.Content, content.MediaType);
    }
}
