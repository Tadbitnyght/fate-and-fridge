using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Household settings routes (docs/API.md §21). Reads require any
/// authenticated actor; writes require the system actor or an Admin.
/// </summary>
public static class SettingsEndpoints
{
    private const string AuthRequiredMessage = "Authentication is required.";
    private const string ForbiddenMessage =
        "The authenticated actor is not allowed to change household settings.";

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings").WithTags("Settings");

        group.MapGet("/household", GetHouseholdSettingsAsync);
        group.MapPatch("/household", PatchHouseholdSettingsAsync);

        return app;
    }

    private static async Task<IResult> GetHouseholdSettingsAsync(
        ICurrentActorProvider actorProvider,
        HouseholdSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);

        // M1-09 refines this with the permission matrix; the contract stays:
        // any authenticated caller may read.
        if (!HouseholdSettingsPolicy.CanRead(actor))
        {
            return AuthRequired();
        }

        var settings = await settingsService.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The household settings row is missing although setup is complete.");

        return Results.Ok(HouseholdSettingsResponse.From(settings));
    }

    private static async Task<IResult> PatchHouseholdSettingsAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        HouseholdSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);

        if (actor is null)
        {
            return AuthRequired();
        }

        if (!HouseholdSettingsPolicy.CanWrite(actor))
        {
            return ApiResults.Error(
                StatusCodes.Status403Forbidden,
                ErrorCodes.Forbidden,
                ForbiddenMessage);
        }

        JsonElement body;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ApiResults.ValidationError(new Dictionary<string, string[]>
            {
                ["body"] = ["The request body must be valid JSON."],
            });
        }

        var validation = HouseholdSettingsPatchValidator.Validate(body);
        if (validation.Patch is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var settings = await settingsService.ApplyPatchAsync(validation.Patch, cancellationToken);
        return Results.Ok(HouseholdSettingsResponse.From(settings));
    }

    private static IResult AuthRequired() =>
        ApiResults.Error(
            StatusCodes.Status401Unauthorized,
            ErrorCodes.AuthRequired,
            AuthRequiredMessage);
}
