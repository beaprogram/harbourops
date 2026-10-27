using System.Net;
using HarbourOps.Api.Features.Comments;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HarbourOps.Tests.Api;

public sealed class CommentTests(PostgresFixture fixture) : FakeClockIntegrationTest(fixture)
{
    private static string CommentsUrl(Guid workOrderId) => $"/api/work-orders/{workOrderId}/comments";

    [Fact]
    public async Task Requester_comments_on_their_request_and_the_manager_reads_it()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        var workOrder = await CreateRequestAsync(riley);

        var posted = await riley.PostJsonAsync<CommentResponse>(CommentsUrl(workOrder.Id), new
        {
            body = "The leak is worse after showers.",
            authorId = DemoAccounts.AlexId,          // ignored: the author is always the signed-in user
            createdAt = "2001-01-01T00:00:00Z",      // ignored: the server clock decides
        });

        var page = await morgan.GetJsonAsync<CommentPage>(CommentsUrl(workOrder.Id));
        var comment = Assert.Single(page.Items);
        Assert.Equal(posted.Id, comment.Id);
        Assert.Equal("The leak is worse after showers.", comment.Body);
        Assert.Equal(DemoAccounts.RileyId, comment.Author.Id);
        Assert.Equal(UserRole.Requester, comment.AuthorRole);
        Assert.True(comment.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Text_is_stored_as_plain_text_with_line_breaks_preserved()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);

        var posted = await riley.PostJsonAsync<CommentResponse>(CommentsUrl(workOrder.Id),
            new { body = "  Line one\r\nLine two\n\n<b>not html</b> & =1+1  " });

        Assert.Equal("Line one\nLine two\n\n<b>not html</b> & =1+1", posted.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("null-byte:\u0000")]
    [InlineData("bell:\u0007")]
    public async Task Empty_or_non_plain_text_comments_are_rejected(string body)
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);

        var response = await riley.PostAsync(CommentsUrl(workOrder.Id), new { body });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("body", (await ApiClient.ReadProblemAsync(response)).Errors!.Keys);
    }

    [Fact]
    public async Task Comments_are_limited_to_2000_characters()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);

        var atLimit = await riley.PostAsync(CommentsUrl(workOrder.Id), new { body = new string('a', Comment.BodyMaxLength) });
        var overLimit = await riley.PostAsync(CommentsUrl(workOrder.Id), new { body = new string('a', Comment.BodyMaxLength + 1) });

        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
    }

    [Theory]
    [InlineData(DemoAccounts.AlexRequester)]     // another requester in the same organization
    [InlineData(DemoAccounts.JordanTechnician)]  // a technician not assigned to it
    [InlineData(DemoAccounts.DanaManager)]       // a manager in another organization
    public async Task Users_who_cannot_see_the_work_order_can_neither_read_nor_comment(string email)
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned); // Riley's request, assigned to Sam
        using var outsider = await SignInAsync(email);

        var read = await outsider.GetAsync(CommentsUrl(workOrder.Id));
        var write = await outsider.PostAsync(CommentsUrl(workOrder.Id), new { body = "Hello?" });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(0, await QueryDbAsync(db => db.Comments.CountAsync(c => c.WorkOrderId == workOrder.Id)));
    }

    [Fact]
    public async Task Technicians_lose_comment_access_when_the_work_is_reassigned()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        Assert.Equal(HttpStatusCode.Created, (await sam.PostAsync(CommentsUrl(workOrder.Id), new { body = "On my way." })).StatusCode);

        await AssignAsync(morgan, workOrder, DemoAccounts.JordanId);

        Assert.Equal(HttpStatusCode.NotFound, (await sam.GetAsync(CommentsUrl(workOrder.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sam.PostAsync(CommentsUrl(workOrder.Id), new { body = "Still mine?" })).StatusCode);
    }

    [Fact]
    public async Task Closed_work_orders_can_be_read_but_not_commented_on()
    {
        var closed = await CreateInStatusAsync(WorkOrderStatus.Closed);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var write = await morgan.PostAsync(CommentsUrl(closed.Id), new { body = "One more thing" });
        var read = await morgan.GetAsync(CommentsUrl(closed.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, write.StatusCode);
        Assert.Equal("work_order_closed", (await ApiClient.ReadProblemAsync(write)).Code);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Comments_do_not_change_the_work_orders_version_timestamp_or_activity()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);
        var activityBefore = await sam.GetJsonAsync<ActivityEventResponse[]>($"/api/work-orders/{workOrder.Id}/activity");

        await riley.PostJsonAsync<CommentResponse>(CommentsUrl(workOrder.Id), new { body = "Any update?" });

        var after = await sam.GetJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}");
        Assert.Equal(workOrder.Version, after.Version);
        Assert.Equal(workOrder.UpdatedAt, after.UpdatedAt);
        Assert.Equal(activityBefore.Length,
            (await sam.GetJsonAsync<ActivityEventResponse[]>($"/api/work-orders/{workOrder.Id}/activity")).Length);
        // The technician's copy is still current, so their workflow command is not rejected as stale.
        Assert.Equal(WorkOrderStatus.InProgress, (await StartAsync(sam, workOrder)).Status);
    }

    [Fact]
    public async Task Pages_are_stable_when_timestamps_tie_and_new_comments_arrive()
    {
        // The fake clock is frozen, so every comment here has the same CreatedAt; Id breaks the tie.
        using var riley = await SignInClockedAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);
        var posted = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            posted.Add((await riley.PostJsonAsync<CommentResponse>(CommentsUrl(workOrder.Id), new { body = $"Comment {i}" })).Id);
        }

        var newest = await riley.GetJsonAsync<CommentPage>($"{CommentsUrl(workOrder.Id)}?limit=2");
        // The new comment arrives later, as it would for a real reader. Without advancing the clock it would
        // share the frozen timestamp, and its random UUIDv7 bits could legitimately sort it into an older page.
        Clock.Advance(TimeSpan.FromSeconds(1));
        await riley.PostJsonAsync<CommentResponse>(CommentsUrl(workOrder.Id), new { body = "Arrives while paging" });
        var middle = await riley.GetJsonAsync<CommentPage>($"{CommentsUrl(workOrder.Id)}?limit=2&before={newest.Items[0].Id}");
        var oldest = await riley.GetJsonAsync<CommentPage>($"{CommentsUrl(workOrder.Id)}?limit=2&before={middle.Items[0].Id}");

        Assert.True(newest.HasOlder);
        Assert.True(middle.HasOlder);
        Assert.False(oldest.HasOlder);
        var paged = oldest.Items.Concat(middle.Items).Concat(newest.Items).Select(c => c.Id).ToList();
        var expected = await QueryDbAsync(db => db.Comments
            .Where(c => posted.Contains(c.Id)).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => c.Id).ToListAsync());
        Assert.Equal(expected, paged);
        Assert.Equal(6, oldest.TotalCount);
    }

    [Fact]
    public async Task Paging_rejects_cursors_from_other_work_orders_and_oversized_pages()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var first = await CreateRequestAsync(riley);
        var second = await CreateRequestAsync(riley);
        var foreign = await riley.PostJsonAsync<CommentResponse>(CommentsUrl(first.Id), new { body = "On the first request" });

        var wrongCursor = await riley.GetAsync($"{CommentsUrl(second.Id)}?before={foreign.Id}");
        var tooLarge = await riley.GetAsync($"{CommentsUrl(second.Id)}?limit=51");

        Assert.Equal(HttpStatusCode.BadRequest, wrongCursor.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
    }

    [Fact]
    public async Task A_comment_racing_a_close_waits_for_it_and_is_rejected()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Resolved);
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);

        // Simulate a manager's close that has updated the row but not committed yet.
        await using var closer = new NpgsqlConnection(Fixture.ConnectionString);
        await closer.OpenAsync(TestContext.Current.CancellationToken);
        await using var closeTransaction = await closer.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var close = new NpgsqlCommand(
            """UPDATE "WorkOrders" SET "Status" = 'Closed', "ClosedAt" = now() WHERE "Id" = @id""", closer, closeTransaction))
        {
            close.Parameters.AddWithValue("id", workOrder.Id);
            Assert.Equal(1, await close.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var comment = riley.PostAsync(CommentsUrl(workOrder.Id), new { body = "Wait, it's leaking again!" });

        // The comment must block on the row lock rather than reading the old "Resolved" status.
        var finishedEarly = await Task.WhenAny(comment, Task.Delay(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken)) == comment;
        Assert.False(finishedEarly, "The comment did not wait for the in-flight close.");

        await closeTransaction.CommitAsync(TestContext.Current.CancellationToken);
        var response = await comment;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("work_order_closed", (await ApiClient.ReadProblemAsync(response)).Code);
        Assert.Equal(0, await QueryDbAsync(db => db.Comments.CountAsync(c => c.WorkOrderId == workOrder.Id)));
    }
}
