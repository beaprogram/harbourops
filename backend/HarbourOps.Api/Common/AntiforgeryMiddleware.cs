using Microsoft.AspNetCore.Antiforgery;

namespace HarbourOps.Api.Common;

/// <summary>
/// Validates the antiforgery token on every state-changing /api request (POST, PUT, PATCH, DELETE),
/// including sign-in and sign-out.
///
/// Why this exists: ASP.NET Core's built-in <c>UseAntiforgery()</c> only checks endpoints that bind
/// form data. JSON endpoints are not protected automatically, so we check explicitly here.
///
/// How it works: the browser receives an HttpOnly antiforgery cookie plus a request token from
/// GET /api/antiforgery/token. The frontend sends that token in the X-XSRF-TOKEN header. A forged
/// cross-site request carries the cookie but cannot read the token, so it fails validation.
/// The token is tied to the signed-in user, so the frontend fetches a new one after login/logout.
/// </summary>
public sealed class AntiforgeryMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-XSRF-TOKEN";

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Request.Path.StartsWithSegments("/api") && !IsSafeMethod(context.Request.Method)
            && !await antiforgery.IsRequestValidAsync(context))
        {
            await ProblemWriter.WriteAsync(context, StatusCodes.Status400BadRequest,
                "Security token missing or expired",
                "The request did not include a valid antiforgery token. Refresh the token and try again.",
                "antiforgery_failed");
            return;
        }

        await next(context);
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}
