using Microsoft.AspNetCore.Mvc;
using PHANTOM.Domain.Common;

namespace PHANTOM.API.Infrastructure;

/// <summary>Maps typed application errors to RFC 9457 problem responses.</summary>
public static class ResultMapping
{
    public static ActionResult Problem(this ControllerBase controller, Error error)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(error);
        var status = error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.InvalidTransition => StatusCodes.Status422UnprocessableEntity,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status500InternalServerError,
        };

        ProblemDetails problem = error.Fields is { Count: > 0 }
            ? new ValidationProblemDetails(error.Fields.ToDictionary(f => f.Key, f => f.Value))
            : new ProblemDetails();
        problem.Status = status;
        problem.Title = error.Message;
        problem.Type = $"https://phantom.local/problems/{error.Code}";
        problem.Extensions["code"] = error.Code;
        problem.Extensions["traceId"] = controller.HttpContext.Items[Middleware.CorrelationIdMiddleware.ItemKey] ?? controller.HttpContext.TraceIdentifier;

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    public static ActionResult<T> ToActionResult<T>(this ControllerBase controller, Result<T> result) =>
        result.IsSuccess ? controller.Ok(result.Value) : controller.Problem(result.Error!);
}
