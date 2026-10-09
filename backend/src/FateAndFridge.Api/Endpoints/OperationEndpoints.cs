using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Batch undo response (docs/M7_PLAN.md §2.2): the operation handle plus the restored days.</summary>
public sealed record BatchUndoResponse(
    CalendarOperationResponse Operation,
    IReadOnlyList<CalendarDayResponse> Days,
    int RevertedCount);

/// <summary>
/// Operation routes (docs/M6_PLAN.md §2.2, docs/M7_PLAN.md §2.2): the temporary
/// Undo for a single reroll and the batch undo for an M7 apply. Both are gated by
/// <c>calendar.edit</c> and return the restored day document(s).
/// </summary>
public static class OperationEndpoints
{
    public static IEndpointRouteBuilder MapOperationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/operations").WithTags("Operations");

        group.MapPost("/{operation_id}/undo", UndoAsync)
            .RequirePermission(PermissionKeys.CalendarEdit);

        var batchGroup = app.MapGroup("/api/v1/batch-operations").WithTags("Operations");

        batchGroup.MapPost("/{batch_operation_id}/undo", BatchUndoAsync)
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

    private static async Task<IResult> BatchUndoAsync(
        string batch_operation_id,
        ICurrentActorProvider actorProvider,
        BatchRerollService batch,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(batch_operation_id, out var batchOperationId))
        {
            return ApiResults.ValidationError(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["batch_operation_id"] = ["batch_operation_id must be a valid UUID."],
                });
        }

        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await batch
            .UndoBatchAsync(batchOperationId, actorUserId, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            // The handle echoes the batch operation that was undone (the frozen
            // BatchUndoOutcome exposes the restored days only; the fresh undo
            // batch id is internal to the history rows).
            BatchUndoOutcome.Success success => Results.Ok(new BatchUndoResponse(
                new CalendarOperationResponse(batchOperationId.ToString(), UndoExpiresAt: null),
                success.Days.Select(CalendarDayResponse.From).ToArray(),
                success.Days.Count)),
            BatchUndoOutcome.Failure failure => ApiResults.Error(
                failure.StatusCode,
                failure.Code,
                failure.Message),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
