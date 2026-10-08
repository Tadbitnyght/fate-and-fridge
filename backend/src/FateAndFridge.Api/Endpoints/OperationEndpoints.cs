using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Operation routes (docs/M6_PLAN.md §2.2): the temporary Undo for a reroll.
/// Undo is gated by <c>calendar.edit</c> and returns the restored day document.
/// </summary>
public static class OperationEndpoints
{
    public static IEndpointRouteBuilder MapOperationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/operations").WithTags("Operations");

        group.MapPost("/{operation_id}/undo", UndoAsync)
            .RequirePermission(PermissionKeys.CalendarEdit);

        return app;
    }

    private static async Task<IResult> UndoAsync(
        string operation_id,
        ICurrentActorProvider actorProvider,
        CalendarRandomizer randomizer,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operation_id, out var operationId))
        {
            return ApiResults.ValidationError(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["operation_id"] = ["operation_id must be a valid UUID."],
                });
        }

        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await randomizer
            .UndoAsync(operationId, actorUserId, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            CalendarUndoOutcome.Success success => Results.Ok(CalendarDayResponse.From(success.Day)),
            CalendarUndoOutcome.Failure failure => ApiResults.Error(
                failure.StatusCode,
                failure.Code,
                failure.Message),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
