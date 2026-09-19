using Microsoft.AspNetCore.Mvc;

namespace NovaWallet.Api;

public static class ProblemDetailsWriter
{
    public static Task WriteAsync(HttpContext httpContext, int status, string errorCode, string title, CancellationToken ct = default)
    {
        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://novawallet.dev/errors/{errorCode}",
        };
        problemDetails.Extensions["errorCode"] = errorCode;
        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;
        problemDetails.Extensions["correlationId"] = httpContext.GetCorrelationId();

        httpContext.Response.StatusCode = status;
        return httpContext.Response.WriteAsJsonAsync(
            problemDetails,
            options: null,
            contentType: "application/problem+json",
            ct);
    }
}
