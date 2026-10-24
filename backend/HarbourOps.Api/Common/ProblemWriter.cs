using Microsoft.AspNetCore.Mvc;

namespace HarbourOps.Api.Common;

/// <summary>
/// Writes RFC 9457 Problem Details from middleware and authentication events, where
/// endpoint results (TypedResults.Problem) are not available.
/// </summary>
public static class ProblemWriter
{
    public static async Task WriteAsync(HttpContext context, int status, string title, string detail, string code)
    {
        context.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Extensions = { ["code"] = code },
        };

        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        var written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        });

        if (!written)
        {
            await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        }
    }
}
