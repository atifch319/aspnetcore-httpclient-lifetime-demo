using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AspNetCoreHttpClientLifetimeDemo.ExceptionHandling;

/// <summary>
/// Maps downstream failures to gateway status codes without writing exception stack traces to the response.
/// Caller cancellation is left untouched so it is not reported as a gateway timeout.
/// </summary>
public sealed class DownstreamExceptionHandler(ILogger<DownstreamExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(exception, "The caller canceled the request before the downstream call completed.");
            return false;
        }

        if (exception is TimeoutException)
        {
            logger.LogWarning(exception, "The downstream request timed out.");
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status504GatewayTimeout,
                "Gateway Timeout",
                "The downstream request timed out.",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "The downstream request failed.");
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status502BadGateway,
                "Bad Gateway",
                "The downstream service request failed.",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static async Task WriteProblemAsync(
        HttpContext httpContext,
        int statusCode,
        string title,
        string detail,
        CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail
            },
            options: null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
