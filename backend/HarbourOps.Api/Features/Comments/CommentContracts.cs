using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;

namespace HarbourOps.Api.Features.Comments;

/// <summary>
/// A new comment. Only the text comes from the client; the author, organization, and timestamp are
/// always taken from the signed-in user and the server clock.
/// </summary>
public sealed record CreateCommentRequest(string? Body)
{
    /// <summary>
    /// Plain text only. Line endings are normalised to "\n" and surrounding whitespace trimmed;
    /// line breaks inside the text are kept. Control characters other than tab and newline are
    /// rejected (PostgreSQL text cannot even store NUL).
    /// </summary>
    public Result<string> Normalize()
    {
        var body = (Body ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (body.Length == 0)
        {
            return Failure.Validation("body", "Write a comment before posting.");
        }

        if (body.Length > Comment.BodyMaxLength)
        {
            return Failure.Validation("body", $"Comments must be {Comment.BodyMaxLength} characters or fewer.");
        }

        if (body.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
        {
            return Failure.Validation("body", "Comments can only contain plain text.");
        }

        return body;
    }
}

public sealed record CommentResponse(
    Guid Id, string Body, PersonSummary Author, UserRole AuthorRole, DateTimeOffset CreatedAt);

/// <summary>
/// A page of comments in chronological order (oldest first). Pages are fetched newest-first:
/// the first request returns the latest comments; pass the oldest returned id as <c>before</c>
/// to load the comments written before it.
/// </summary>
public sealed record CommentPage(IReadOnlyList<CommentResponse> Items, bool HasOlder, int TotalCount);
