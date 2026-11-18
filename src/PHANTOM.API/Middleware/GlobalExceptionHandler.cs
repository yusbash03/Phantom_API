using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PHANTOM.API.Middleware;

/// <summary>Logs unexpected exceptions and returns a generic problem response without internals.</summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetails, IHostEnvironment environment)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var isConcurrency = exception is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException;
        var status = isConcurrency ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError;
        if (isConcurrency)
        {
            logger.LogWarning(exception, "Concurrency conflict on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = isConcurrency
                ? "This record was changed by someone else. Reload and try again."
                : "An unexpected error occurred.",
            Detail = environment.IsDevelopment() ? exception.Message : null,
        };
        problem.Extensions["code"] = isConcurrency ? "concurrency.conflict" : "server.error";

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
    }
}
