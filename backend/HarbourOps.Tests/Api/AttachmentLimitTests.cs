using System.Net;
using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Attachments.Storage;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HarbourOps.Tests.Api;

/// <summary>
/// Limits, races, and failure cleanup, with small limits so tests stay fast. Storage and the database
/// don't share a transaction, so these tests check that no photo becomes visible half-saved and that
/// stored files are removed whenever the database row isn't committed.
/// </summary>
public sealed class AttachmentLimitTests(PostgresFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    private const int MaxFileSize = 40_000;
    private HarbourOpsFactory _app = null!;

    public ValueTask InitializeAsync()
    {
        _app = new HarbourOpsFactory(Fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Attachments:MaxFileSizeBytes"] = MaxFileSize.ToString(),
            ["Attachments:MaxPerWorkOrder"] = "2",
            ["Attachments:MaxDimension"] = "400",
            ["Attachments:MaxPixels"] = "100000",
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static string AttachmentsUrl(Guid workOrderId) => $"/api/work-orders/{workOrderId}/attachments";

    private int StoredFiles() => AttachmentTests.StoredFiles(_app.StoragePath);

    private Task<int> RowsFor(Guid workOrderId) =>
        QueryDbAsync(db => db.Attachments.CountAsync(a => a.WorkOrderId == workOrderId));

    private async Task<(ApiClient Riley, WorkOrderDetailResponse WorkOrder)> NewRequestAsync()
    {
        var riley = await _app.SignInAsync(DemoAccounts.RileyRequester);
        return (riley, await CreateRequestAsync(riley));
    }

    [Fact]
    public async Task Files_over_the_size_limit_are_refused_with_413()
    {
        var (riley, workOrder) = await NewRequestAsync();
        using var _ = riley;
        var big = new byte[MaxFileSize + 1];
        TestImages.Png(10, 10).CopyTo(big, 0);

        var response = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), big, "huge.png", "image/png");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file_too_large", (await ApiClient.ReadProblemAsync(response)).Code);
    }

    [Theory]
    [InlineData(401, 20)]   // longer than MaxDimension (400)
    [InlineData(350, 350)]  // 122,500 pixels > MaxPixels (100,000)
    public async Task Images_over_the_dimension_or_pixel_limit_are_refused(int width, int height)
    {
        var (riley, workOrder) = await NewRequestAsync();
        using var _ = riley;

        var response = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Png(width, height), "big.png", "image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("image_too_large", (await ApiClient.ReadProblemAsync(response)).Code);
    }

    [Fact]
    public async Task The_photo_count_limit_is_enforced()
    {
        var (riley, workOrder) = await NewRequestAsync();
        using var _ = riley;
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(50, 50))).StatusCode);
        }

        var third = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(50, 50));
        var list = await riley.GetJsonAsync<AttachmentList>(AttachmentsUrl(workOrder.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);
        Assert.Equal("attachment_limit_reached", (await ApiClient.ReadProblemAsync(third)).Code);
        Assert.False(list.CanUpload);
        Assert.Equal(2, list.Limits.MaxPerWorkOrder);
    }

    [Fact]
    public async Task Concurrent_uploads_cannot_exceed_the_limit_and_the_loser_leaves_no_files()
    {
        var (riley, workOrder) = await NewRequestAsync();
        using var _ = riley;
        await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(50, 50)); // 1 of 2

        // Hold the work-order row lock so both uploads reach the locking step and queue behind it.
        await using var holder = await LockWorkOrderAsync(workOrder.Id, "SELECT 1 FROM \"WorkOrders\" WHERE \"Id\" = @id FOR NO KEY UPDATE");
        var first = riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(60, 60));
        var second = riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(70, 70));
        await WaitForWaitingLocksAsync(2);
        await holder.Transaction.CommitAsync(TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(first, second);

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Equal(2, await RowsFor(workOrder.Id));
        Assert.Equal(2 * 2, StoredFiles()); // two photos × (full + preview); the loser's files were removed
    }

    [Fact]
    public async Task An_upload_racing_a_close_waits_for_it_and_is_rejected_without_leaving_files()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Resolved);
        using var riley = await _app.SignInAsync(DemoAccounts.RileyRequester);

        // A manager's close that has updated the row but not committed yet.
        await using var closer = await LockWorkOrderAsync(workOrder.Id,
            "UPDATE \"WorkOrders\" SET \"Status\" = 'Closed', \"ClosedAt\" = now() WHERE \"Id\" = @id");
        var upload = riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(80, 80));

        await WaitForWaitingLocksAsync(1); // the upload is blocked on the close's row lock, not reading old data
        Assert.False(upload.IsCompleted, "The upload did not wait for the in-flight close.");
        await closer.Transaction.CommitAsync(TestContext.Current.CancellationToken);
        var response = await upload;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("work_order_closed", (await ApiClient.ReadProblemAsync(response)).Code);
        Assert.Equal(0, await RowsFor(workOrder.Id));
        Assert.Equal(0, StoredFiles());
    }

    [Fact]
    public async Task If_the_database_insert_fails_the_stored_files_are_removed()
    {
        var (riley, workOrder) = await NewRequestAsync();
        using var _ = riley;
        var function = $"fail_attachment_{workOrder.Id:N}";
        var createTrigger = $"""
            CREATE FUNCTION {function}() RETURNS trigger AS $$
            BEGIN
              IF NEW."WorkOrderId" = '{workOrder.Id}' THEN RAISE EXCEPTION 'simulated database failure'; END IF;
              RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER {function} AFTER INSERT ON "Attachments"
              DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {function}();
            """;
        var dropTrigger = $"""DROP TRIGGER {function} ON "Attachments"; DROP FUNCTION {function}();""";
        await QueryDbAsync(db => db.Database.ExecuteSqlRawAsync(createTrigger));

        HttpResponseMessage response;
        try
        {
            response = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(90, 90));
        }
        finally
        {
            await QueryDbAsync(db => db.Database.ExecuteSqlRawAsync(dropTrigger));
        }

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await RowsFor(workOrder.Id));
        Assert.Equal(0, StoredFiles());
    }

    [Fact]
    public async Task If_storage_fails_partway_the_upload_reports_503_and_nothing_is_kept()
    {
        await using var failingApp = new HarbourOpsFactory(Fixture.ConnectionString,
            configureServices: services => services.AddSingleton<IAttachmentStorage>(sp =>
                new FailOnSecondWriteStorage(ActivatorUtilities.CreateInstance<LocalFileAttachmentStorage>(sp))));
        using var riley = await failingApp.SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);

        var response = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(90, 90));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("storage_unavailable", (await ApiClient.ReadProblemAsync(response)).Code);
        Assert.Equal(0, await RowsFor(workOrder.Id));
        Assert.Equal(0, AttachmentTests.StoredFiles(failingApp.StoragePath)); // the first (successful) write was cleaned up
    }

    [Fact]
    public async Task Uploads_beyond_the_processing_and_queue_bound_get_503_until_a_slot_frees()
    {
        var storage = new GatedStorage();
        await using var busyApp = new HarbourOpsFactory(Fixture.ConnectionString,
            new Dictionary<string, string?>
            {
                ["Attachments:MaxConcurrentProcessing"] = "1",
                ["Attachments:MaxQueuedUploads"] = "0",
            },
            services => services.AddSingleton<IAttachmentStorage>(sp =>
                storage.Wrap(ActivatorUtilities.CreateInstance<LocalFileAttachmentStorage>(sp))));
        using var riley = await busyApp.SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);

        // The first upload holds the only slot while its storage write is blocked.
        var first = riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(60, 60));
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var refused = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(60, 60));
        storage.Release();
        var admitted = await first;
        var afterwards = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(60, 60));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("uploads_busy", (await ApiClient.ReadProblemAsync(refused)).Code);
        Assert.Equal("5", refused.Headers.RetryAfter!.ToString());
        Assert.Equal(HttpStatusCode.Created, admitted.StatusCode);
        Assert.Equal(HttpStatusCode.Created, afterwards.StatusCode); // the slot was released
        Assert.Equal(2, await QueryDbAsync(db => db.Attachments.CountAsync(a => a.WorkOrderId == workOrder.Id)));
    }

    [Fact]
    public async Task Reconciliation_removes_only_old_unreferenced_objects()
    {
        var (riley, workOrder) = await NewRequestAsync();
        using var _ = riley;
        await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(50, 50));
        var referenced = await QueryDbAsync(db => db.Attachments.Where(a => a.WorkOrderId == workOrder.Id)
            .Select(a => new { a.StorageKey, a.PreviewStorageKey }).SingleAsync());

        await using var scope = _app.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        var oldOrphan = StorageKeys.New();
        var newOrphan = StorageKeys.New();
        await storage.SaveAsync(oldOrphan, new byte[] { 1 }, TestContext.Current.CancellationToken);
        await storage.SaveAsync(newOrphan, new byte[] { 1 }, TestContext.Current.CancellationToken);
        var oldPartial = Path.Combine(_app.StoragePath, ".incoming", "crashed-upload.part");
        await File.WriteAllBytesAsync(oldPartial, [1], TestContext.Current.CancellationToken);
        // Age everything except the new orphan past the 24-hour minimum, including the referenced photo.
        foreach (var key in new[] { oldOrphan, referenced.StorageKey, referenced.PreviewStorageKey })
        {
            File.SetLastWriteTimeUtc(Path.Combine(_app.StoragePath, key[..2], key), DateTime.UtcNow.AddDays(-3));
        }

        File.SetLastWriteTimeUtc(oldPartial, DateTime.UtcNow.AddDays(-3));

        var report = await scope.ServiceProvider.GetRequiredService<AttachmentReconciler>().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.OrphansDeleted);
        Assert.Equal(1, report.PartialWritesDeleted);
        Assert.Null(await storage.OpenReadAsync(oldOrphan, TestContext.Current.CancellationToken));
        Assert.NotNull(await storage.OpenReadAsync(newOrphan, TestContext.Current.CancellationToken)); // too recent to judge
        await using var full = await storage.OpenReadAsync(referenced.StorageKey, TestContext.Current.CancellationToken);
        await using var preview = await storage.OpenReadAsync(referenced.PreviewStorageKey, TestContext.Current.CancellationToken);
        Assert.NotNull(full);    // referenced objects are never removed, however old
        Assert.NotNull(preview);
    }

    /// <summary>Opens a raw connection that runs <paramref name="sql"/> in a transaction and keeps it open.</summary>
    private async Task<LockHolder> LockWorkOrderAsync(Guid workOrderId, string sql)
    {
        var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", workOrderId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new LockHolder(connection, transaction);
    }

    /// <summary>Waits until PostgreSQL reports <paramref name="count"/> sessions blocked on a lock.</summary>
    private async Task WaitForWaitingLocksAsync(int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()", connection);
            if ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))! >= count)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Expected {count} upload(s) to be waiting on the work-order row lock.");
    }

    private sealed record LockHolder(NpgsqlConnection Connection, NpgsqlTransaction Transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>Lets the first write succeed and fails the second, like a disk filling up mid-upload.</summary>
    private sealed class FailOnSecondWriteStorage(IAttachmentStorage inner) : IAttachmentStorage
    {
        private int _writes;

        public Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            Interlocked.Increment(ref _writes) == 2
                ? throw new IOException("No space left on device (simulated).")
                : inner.SaveAsync(key, content, ct);

        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => inner.OpenReadAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);
        public IAsyncEnumerable<StoredObjectInfo> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<int> DeleteAbandonedPartialWritesAsync(DateTimeOffset olderThan, CancellationToken ct) =>
            inner.DeleteAbandonedPartialWritesAsync(olderThan, ct);
    }
}
