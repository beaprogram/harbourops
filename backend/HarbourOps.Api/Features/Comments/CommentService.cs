using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Demo.Sandbox;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Api.Features.Comments;

/// <summary>
/// Reading and adding comments. Visibility is the same as for the work order itself
/// (<see cref="WorkOrderQueries.VisibleTo"/>): managers see their whole organization, technicians only
/// work currently assigned to them, requesters only their own requests.
/// </summary>
public sealed class CommentService(AppDbContext db, TimeProvider clock, DemoQuotas demoQuotas, ILogger<CommentService> logger)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    private static readonly Failure NotFound = Failure.NotFound("Work order not found.");

    public async Task<Result<CommentPage>> ListAsync(
        CurrentUser user, Guid workOrderId, int limit, Guid? before, CancellationToken ct)
    {
        if (!await db.WorkOrders.VisibleTo(user).AnyAsync(w => w.Id == workOrderId, ct))
        {
            return NotFound;
        }

        var comments = db.Comments.AsNoTracking()
            .Where(c => c.OrganizationId == user.OrganizationId && c.WorkOrderId == workOrderId);
        var totalCount = await comments.CountAsync(ct);

        var page = comments;
        if (before is { } cursorId)
        {
            var cursor = await comments.Where(c => c.Id == cursorId)
                .Select(c => new { c.CreatedAt, c.Id })
                .SingleOrDefaultAsync(ct);
            if (cursor is null)
            {
                return Failure.Validation("before", "That comment does not belong to this work order.");
            }

            // Keyset pagination: a row-value comparison on (CreatedAt, Id). New comments arriving while
            // someone reads older pages never shift or duplicate rows, unlike OFFSET paging.
            page = page.Where(c => EF.Functions.LessThan(
                ValueTuple.Create(c.CreatedAt, c.Id), ValueTuple.Create(cursor.CreatedAt, cursor.Id)));
        }

        var newestFirst = await page
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(limit + 1)
            .Select(c => new CommentResponse(
                c.Id, c.Body, new PersonSummary(c.Author.Id, c.Author.DisplayName), c.Author.Role, c.CreatedAt))
            .ToListAsync(ct);

        var hasOlder = newestFirst.Count > limit;
        var items = newestFirst.Take(limit).Reverse().ToList();
        return new CommentPage(items, hasOlder, totalCount);
    }

    /// <summary>
    /// Adds a comment. The visibility and Closed checks run while holding a FOR SHARE lock on the work
    /// order row, inside the same transaction as the insert. Closing (an UPDATE) needs a conflicting
    /// row lock, so the two are serialised:
    ///   • If a close commits first, this lock waits, then re-reads the row, sees Closed, and rejects.
    ///   • If this comment locks first, the close waits until the comment commits, so in commit order the
    ///     comment precedes the close.
    /// A plain read would see the pre-close status and could insert a comment on closed work.
    /// The lock does not change the row's xmin, so comments never invalidate anyone's Version.
    /// </summary>
    public async Task<Result<CommentResponse>> AddAsync(
        CurrentUser user, Guid workOrderId, CreateCommentRequest request, CancellationToken ct)
    {
        var body = request.Normalize();
        if (body.Failure is { } invalid)
        {
            return invalid;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var target = await db.WorkOrders
            .FromSql($"""SELECT * FROM "WorkOrders" WHERE "Id" = {workOrderId} FOR SHARE""")
            .VisibleTo(user)
            .Select(w => new { w.Status })
            .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            return NotFound;
        }

        if (target.Status == WorkOrderStatus.Closed)
        {
            return Failure.InvalidState("This work order is closed, so it can't receive new comments.", "work_order_closed");
        }

        if (await demoQuotas.TryUseAsync(user, DemoQuota.Comment, ct) is { } limited)
        {
            return limited;
        }

        var now = clock.GetUtcNow();
        var comment = new Comment
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = user.OrganizationId,
            WorkOrderId = workOrderId,
            AuthorId = user.Id,
            Body = body.Value!,
            CreatedAt = now,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Comment {CommentId} added to work order {WorkOrderId} by user {UserId}", comment.Id, workOrderId, user.Id);

        return new CommentResponse(comment.Id, comment.Body, new PersonSummary(user.Id, user.DisplayName), user.Role, now);
    }
}
