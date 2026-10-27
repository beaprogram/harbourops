using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Attachments.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace HarbourOps.Tests.Workflow;

public sealed class AttachmentStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbourops-storage-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("photo.jpg")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]   // keys are lowercase
    [InlineData("")]
    public async Task Only_server_generated_keys_are_accepted(string key)
    {
        var storage = Storage();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(key, new byte[] { 1 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Objects_round_trip_and_deleting_twice_is_harmless()
    {
        var storage = Storage();
        var key = StorageKeys.New();

        await storage.SaveAsync(key, new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        await using (var stream = await storage.OpenReadAsync(key, TestContext.Current.CancellationToken))
        {
            using var copy = new MemoryStream();
            await stream!.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        }

        await storage.DeleteAsync(key, TestContext.Current.CancellationToken);
        await storage.DeleteAsync(key, TestContext.Current.CancellationToken);
        Assert.Null(await storage.OpenReadAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_an_object_that_never_existed_is_a_no_op_even_without_its_shard_directory()
    {
        var storage = Storage();
        var key = StorageKeys.New();
        Assert.False(Directory.Exists(Path.Combine(_root, key[..2])));

        await storage.DeleteAsync(key, TestContext.Current.CancellationToken); // cleanup retries rely on this

        Assert.Null(await storage.OpenReadAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Abandoned_partial_writes_are_cleaned_up_only_when_old()
    {
        var storage = Storage();
        var incoming = Path.Combine(_root, ".incoming");
        var old = Path.Combine(incoming, "old.part");
        var recent = Path.Combine(incoming, "recent.part");
        await File.WriteAllBytesAsync(old, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(recent, [1], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));

        var deleted = await storage.DeleteAbandonedPartialWritesAsync(DateTimeOffset.UtcNow.AddDays(-1), TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Storage_inside_the_web_root_is_refused()
    {
        var webRoot = Path.Combine(_root, "wwwroot");

        var exception = Assert.Throws<InvalidOperationException>(() => Storage(Path.Combine(webRoot, "uploads"), webRoot));
        Assert.Contains("web root", exception.Message);
    }

    private LocalFileAttachmentStorage Storage(string? path = null, string? webRoot = null) =>
        new(Options.Create(new AttachmentOptions { Storage = { LocalPath = path ?? _root } }),
            new TestEnvironment(webRoot ?? Path.Combine(_root, "..", "public")));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "HarbourOps.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
    }
}
