using System.Text.Json;
using System.Text.Json.Serialization;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Setup completion response (docs/API.md §21): the recovery key is returned
/// exactly once here; <see cref="Session"/> is present only in multiuser mode.
/// </summary>
public sealed record SetupCompleteResponse(
    string AuthMode,
    string RecoveryKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthSessionResponse? Session);

/// <summary>Setup routes (docs/API.md §21). Public while setup is incomplete.</summary>
public static class SetupEndpoints
{
    private const string AlreadyCompletedMessage = "Setup has already been completed.";

    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/setup").WithTags("Setup");

        group.MapGet("/status", async (SetupStateService setupState, CancellationToken cancellationToken) =>
            Results.Ok(await setupState.GetStatusAsync(cancellationToken)));

        // Public, single-shot, atomic (docs/API.md §21): 201 on success, 409 on
        // repeat, 400 with field errors on invalid input.
        group.MapPost("/complete", CompleteSetupAsync);

        return app;
    }

    private static async Task<IResult> CompleteSetupAsync(
        HttpContext context,
        SetupStateService setupState,
        SetupCompletionService setupCompletion,
        PermissionService permissionService,
        IAntiforgery antiforgery,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        // The response carries the one-time recovery key: never cache it.
        ResponseCache.NoStore(context);

        // Repeats return 409 before body parsing/validation so a malformed body
        // on an already-configured instance still yields SETUP_ALREADY_COMPLETED.
        // The authoritative, race-safe check happens inside the setup
        // transaction under the advisory lock.
        if (await setupState.IsSetupCompletedAsync(cancellationToken))
        {
            return AlreadyCompleted();
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return ApiResults.ValidationError(new Dictionary<string, string[]>
            {
                ["body"] = ["The request body must be a valid JSON object."],
            });
        }

        using (document)
        {
            var validation = SetupCompletionValidator.Validate(document.RootElement);
            if (validation.Request is null)
            {
                return ApiResults.ValidationError(validation.FieldErrors);
            }

            var result = await setupCompletion.CompleteAsync(
                validation.Request,
                context.Connection.RemoteIpAddress?.ToString(),
                UserAgent(context),
                cancellationToken);

            return result switch
            {
                SetupCompletionResult.AlreadyCompleted => AlreadyCompleted(),
                SetupCompletionResult.RecoveryKeyUnusable unusable => ApiResults.Error(
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.InternalError,
                    unusable.Message),
                SetupCompletionResult.Success success => await SetupCompletedAsync(
                    context,
                    success,
                    permissionService,
                    antiforgery,
                    jsonOptions,
                    cancellationToken),
                _ => throw new InvalidOperationException("Unknown setup completion result."),
            };
        }
    }

    private static IResult AlreadyCompleted() =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.SetupAlreadyCompleted,
            AlreadyCompletedMessage);

    private static async Task<IResult> SetupCompletedAsync(
        HttpContext context,
        SetupCompletionResult.Success success,
        PermissionService permissionService,
        IAntiforgery antiforgery,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        AuthSessionResponse? session = null;
        if (success.Admin is { } admin
            && success.Session is { } userSession
            && success.RawSessionToken is { } rawToken)
        {
            // Same sign-in as a successful login: cookie, principal, CSRF pair.
            SessionSignIn.Apply(context, admin, userSession, rawToken, antiforgery);
            session = await SessionPayloads.ForUserAsync(permissionService, admin, cancellationToken);
        }

        var payload = new SetupCompleteResponse(
            EnumTextConverters.AuthModeToText(success.AuthMode),
            success.RecoveryKey,
            session);

        return Results.Json(
            payload,
            jsonOptions.Value.SerializerOptions,
            statusCode: StatusCodes.Status201Created);
    }

    private static string? UserAgent(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }
}
