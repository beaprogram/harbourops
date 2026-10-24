namespace HarbourOps.Api.Common;

/// <summary>Baseline browser security headers for the API and the built single-page app.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    // The production bundle has no inline scripts or styles, so a strict policy works.
    // (Development is skipped because the Vite dev server and the API reference page inject inline code.)
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (!environment.IsDevelopment())
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
        }

        return next(context);
    }
}
