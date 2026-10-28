using FateAndFridge.Api.Errors;

namespace FateAndFridge.Api.Middleware;

/// <summary>
/// Last-resort exception handler (docs/API.md §4): unexpected exceptions become
/// <c>500 INTERNAL_ERROR</c> in the shared envelope, with no stack trace
/// exception type, or details leaked to the client. Explicit error envelopes
/// produced by endpoints are untouched because they never throw.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        // A client disconnect surfaces as OperationCanceledException; it is not a
        // server error, so it is rethrown instead of being logged as a 500.
        catch (Exception exception) when (exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(
                exception,
                "Unhandled exception for {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            if (context.Response.HasStarted)
            {
                // The response is already on the wire; nothing safe left to do.
                throw;
            }

            context.Response.Clear();
            await ApiResults.Error(
                StatusCodes.Status500InternalServerError,
                ErrorCodes.InternalError,
                "An unexpected server error occurred.").ExecuteAsync(context);
        }
    }
}
