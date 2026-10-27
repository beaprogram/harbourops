using System.Net;
using System.Text;
using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

/// <summary>Photo uploads and delivery: permissions, validation, headers, and isolation, on real PostgreSQL.</summary>
public sealed class AttachmentTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private static string AttachmentsUrl(Guid workOrderId) => $"/api/work-orders/{workOrderId}/attachments";

    private int StoredFileCount() => StoredFiles(Factory.StoragePath);

    internal static int StoredFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(f => !f.Contains($"{Path.DirectorySeparatorChar}.incoming"))
            : 0;

    [Fact]
    public async Task Requester_uploads_a_photo_and_the_manager_sees_and_downloads_it()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        var workOrder = await CreateRequestAsync(riley);

        var upload = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(1200, 900), "leak under sink.jpg");
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var list = await morgan.GetJsonAsync<AttachmentList>(AttachmentsUrl(workOrder.Id));
        var photo = Assert.Single(list.Items);
        Assert.Equal("leak under sink.jpg", photo.FileName);
        Assert.Equal(DemoAccounts.RileyId, photo.UploadedBy.Id);
        Assert.Equal(UserRole.Requester, photo.UploaderRole);
        Assert.Equal((1200, 900), (photo.Width, photo.Height));
        Assert.True(photo.PreviewWidth <= 480 && photo.PreviewHeight <= 480);
        Assert.True(list.CanUpload);

        var preview = await morgan.GetBytesAsync(photo.PreviewUrl);
        var full = await morgan.GetBytesAsync(photo.ContentUrl);
        Assert.Equal(photo.SizeBytes, full.Length);
        Assert.True(preview.Length < full.Length);
    }

    [Fact]
    public async Task Photo_responses_have_safe_headers()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);
        await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(200, 100), "Fuite cuisine é.jpg");
        var photo = Assert.Single((await riley.GetJsonAsync<AttachmentList>(AttachmentsUrl(workOrder.Id))).Items);

        var inline = await riley.GetAsync(photo.ContentUrl);
        var download = await riley.GetAsync(photo.ContentUrl + "?download=true");

        Assert.Equal("image/jpeg", inline.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", inline.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("Fuite cuisine é.jpg", inline.Content.Headers.ContentDisposition.FileNameStar);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", inline.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", inline.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.True(inline.Headers.CacheControl!.Private);
        Assert.True(inline.Headers.CacheControl.NoStore);
    }

    [Fact]
    public async Task Hostile_file_names_cannot_inject_headers_or_paths()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);
        // Hand-built part header so the name keeps its quotes, semicolons, path, and right-to-left override.
        var boundary = "harbourops-boundary";
        var head = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"..\\\\..\\\\evil;‮gpj.png\"\r\n" +
            "Content-Type: image/jpeg\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        var body = new ByteArrayContent([.. head, .. TestImages.Jpeg(64, 64), .. tail]);
        body.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        var upload = await riley.SendAsync(AttachmentsUrl(workOrder.Id), body);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var photo = Assert.Single((await riley.GetJsonAsync<AttachmentList>(AttachmentsUrl(workOrder.Id))).Items);
        var response = await riley.GetAsync(photo.ContentUrl);

        Assert.Equal("evilgpj.jpg", photo.FileName);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Single(response.Content.Headers.GetValues("Content-Disposition"));
        var stored = await QueryDbAsync(db => db.Attachments.SingleAsync(a => a.Id == photo.Id));
        Assert.Matches("^[0-9a-f]{32}$", stored.StorageKey); // never derived from the submitted name
    }

    [Theory]
    [InlineData("no-file-part")]
    [InlineData("empty-file")]
    [InlineData("file-after-four-parts")]
    [InlineData("missing-closing-boundary")]
    public async Task Malformed_multipart_bodies_get_a_field_error_and_store_nothing(string shape)
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);
        var filesBefore = StoredFileCount();
        const string boundary = "harbourops-boundary";
        static string Field(string name) => $"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\nvalue\r\n";
        static string FileHead() => $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.jpg\"\r\n\r\n";
        var end = $"\r\n--{boundary}--\r\n";
        var photo = Encoding.Latin1.GetString(TestImages.Jpeg(64, 64));
        var raw = shape switch
        {
            "no-file-part" => Field("note") + $"--{boundary}--\r\n",
            "empty-file" => FileHead() + end,
            "file-after-four-parts" => Field("a") + Field("b") + Field("c") + Field("d") + FileHead() + photo + end,
            "missing-closing-boundary" => FileHead() + photo,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var body = new ByteArrayContent(Encoding.Latin1.GetBytes(raw));
        body.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        var response = await riley.SendAsync(AttachmentsUrl(workOrder.Id), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", (await ApiClient.ReadProblemAsync(response)).Errors!.Keys);
        Assert.Equal(filesBefore, StoredFileCount());
        Assert.Equal(0, await QueryDbAsync(db => db.Attachments.CountAsync(a => a.WorkOrderId == workOrder.Id)));
    }

    [Fact]
    public async Task Png_content_is_accepted_even_when_named_and_typed_as_jpeg_and_stored_as_jpeg()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);

        var upload = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Png(300, 200), "screenshot.jpg", "image/jpeg");

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var stored = await QueryDbAsync(db => db.Attachments.SingleAsync(a => a.WorkOrderId == workOrder.Id));
        Assert.Equal("image/png", stored.SourceMediaType);
        Assert.Equal("image/jpeg", stored.MediaType);
    }

    [Theory]
    [InlineData("<html><body><script>alert('x')</script></body></html>", "photo.jpg", "image/jpeg", HttpStatusCode.UnsupportedMediaType, "unsupported_image")]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000\u0000\u0000\u0000", "photo.png", "image/png", HttpStatusCode.UnsupportedMediaType, "unsupported_image")]
    [InlineData("ÿØÿàgarbage that only looks like a JPEG", "photo.jpg", "image/jpeg", HttpStatusCode.BadRequest, "invalid_image")]
    public async Task Deceptive_or_damaged_files_are_rejected_and_nothing_is_stored(
        string content, string fileName, string contentType, HttpStatusCode expectedStatus, string expectedCode)
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);
        var filesBefore = StoredFileCount();

        var response = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), Encoding.Latin1.GetBytes(content), fileName, contentType);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, (await ApiClient.ReadProblemAsync(response)).Code);
        Assert.Equal(filesBefore, StoredFileCount());
        Assert.Equal(0, await QueryDbAsync(db => db.Attachments.CountAsync(a => a.WorkOrderId == workOrder.Id)));
    }

    [Fact]
    public async Task Truncated_images_are_rejected()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);

        var response = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Truncated(TestImages.Jpeg(800, 600)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", (await ApiClient.ReadProblemAsync(response)).Errors!.Keys);
    }

    [Fact]
    public async Task Uploads_must_be_multipart_and_carry_the_antiforgery_header()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);
        var filesBefore = StoredFileCount();

        var json = await riley.PostAsync(AttachmentsUrl(workOrder.Id), new { file = "not a file" });
        var noToken = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(64, 64), withAntiforgery: false);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal("antiforgery_failed", (await ApiClient.ReadProblemAsync(noToken)).Code);
        Assert.Equal(filesBefore, StoredFileCount());
    }

    [Theory]
    [InlineData(DemoAccounts.AlexRequester)]     // another requester in the same organization
    [InlineData(DemoAccounts.JordanTechnician)]  // a technician not assigned to the work
    [InlineData(DemoAccounts.DanaManager)]       // a manager in another organization
    public async Task Users_who_cannot_see_the_work_order_cannot_upload_list_or_retrieve_photos(string email)
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned); // Riley's request, assigned to Sam
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(100, 100));
        var photo = Assert.Single((await riley.GetJsonAsync<AttachmentList>(AttachmentsUrl(workOrder.Id))).Items);
        using var outsider = await SignInAsync(email);

        var upload = await outsider.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(100, 100));
        var list = await outsider.GetAsync(AttachmentsUrl(workOrder.Id));
        var preview = await outsider.GetAsync(photo.PreviewUrl);
        var content = await outsider.GetAsync(photo.ContentUrl);
        var missing = await outsider.GetAsync($"{AttachmentsUrl(workOrder.Id)}/{Guid.NewGuid()}/content");

        Assert.All(new[] { upload, list, preview, content, missing }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        // An existing photo and a non-existent one are indistinguishable.
        Assert.Equal((await ApiClient.ReadProblemAsync(missing)).Detail, (await ApiClient.ReadProblemAsync(content)).Detail);
        Assert.Equal(1, await QueryDbAsync(db => db.Attachments.CountAsync(a => a.WorkOrderId == workOrder.Id)));
    }

    [Fact]
    public async Task Photos_cannot_be_fetched_through_another_work_orders_url()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var first = await CreateRequestAsync(riley);
        var second = await CreateRequestAsync(riley);
        await riley.UploadAsync(AttachmentsUrl(first.Id), TestImages.Jpeg(100, 100));
        var photo = Assert.Single((await riley.GetJsonAsync<AttachmentList>(AttachmentsUrl(first.Id))).Items);

        var mismatched = await riley.GetAsync($"{AttachmentsUrl(second.Id)}/{photo.Id}/content");

        Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
    }

    [Fact]
    public async Task Technicians_lose_photo_access_when_the_work_is_reassigned()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);
        using var jordan = await SignInAsync(DemoAccounts.JordanTechnician);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        Assert.Equal(HttpStatusCode.Created, (await sam.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(100, 100))).StatusCode);
        var photo = Assert.Single((await sam.GetJsonAsync<AttachmentList>(AttachmentsUrl(workOrder.Id))).Items);

        await AssignAsync(morgan, workOrder, DemoAccounts.JordanId);

        Assert.Equal(HttpStatusCode.NotFound, (await sam.GetAsync(photo.PreviewUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sam.GetAsync(AttachmentsUrl(workOrder.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await jordan.GetAsync(photo.PreviewUrl)).StatusCode);
    }

    [Fact]
    public async Task Closed_work_orders_keep_their_photos_readable_but_refuse_new_ones()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Resolved);
        await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(100, 100));
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        var latest = await morgan.GetJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}");
        await morgan.PostJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}/close", new { version = latest.Version });

        var upload = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(100, 100));
        var list = await riley.GetJsonAsync<AttachmentList>(AttachmentsUrl(workOrder.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.StatusCode);
        Assert.Equal("work_order_closed", (await ApiClient.ReadProblemAsync(upload)).Code);
        Assert.False(list.CanUpload);
        Assert.Equal(HttpStatusCode.OK, (await riley.GetAsync(Assert.Single(list.Items).PreviewUrl)).StatusCode);
    }

    [Fact]
    public async Task Photos_do_not_change_the_work_orders_version_timestamp_or_activity()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);
        var activityBefore = await sam.GetJsonAsync<ActivityEventResponse[]>($"/api/work-orders/{workOrder.Id}/activity");

        await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(100, 100));

        var after = await sam.GetJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}");
        Assert.Equal(workOrder.Version, after.Version);
        Assert.Equal(workOrder.UpdatedAt, after.UpdatedAt);
        Assert.Equal(activityBefore.Length,
            (await sam.GetJsonAsync<ActivityEventResponse[]>($"/api/work-orders/{workOrder.Id}/activity")).Length);
        Assert.Equal(WorkOrderStatus.InProgress, (await StartAsync(sam, workOrder)).Status); // not stale
    }

    [Fact]
    public async Task Database_constraints_reject_attachments_pointing_across_organizations()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.New); // Harbourview

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => QueryDbAsync(async db =>
        {
            db.Attachments.Add(new Attachment
            {
                Id = Guid.NewGuid(),
                OrganizationId = DemoAccounts.CoveCommonsId, // wrong organization for this work order
                WorkOrderId = workOrder.Id,
                UploadedById = DemoAccounts.CaseyId,
                UploadedAt = DateTimeOffset.UtcNow,
                FileName = "x.jpg",
                MediaType = "image/jpeg",
                SourceMediaType = "image/jpeg",
                StorageKey = new string('a', 32),
                SizeBytes = 1, Width = 1, Height = 1,
                PreviewStorageKey = new string('b', 32),
                PreviewSizeBytes = 1, PreviewWidth = 1, PreviewHeight = 1,
            });
            return await db.SaveChangesAsync();
        }));

        Assert.Contains("FK_Attachments_WorkOrders_OrganizationId_WorkOrderId", exception.InnerException!.Message);
    }
}
