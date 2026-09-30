using System.Text.Json;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Household settings routes (docs/API.md §21). Reads require any
/// authenticated actor; writes require the system actor or an Admin. Both
/// requirements are enforced by the M1-09 permission filters.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings").WithTags("Settings");

        group.MapGet("/household", GetHouseholdSettingsAsync).RequireAuthenticated();
        group.MapPatch("/household", PatchHouseholdSettingsAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> GetHouseholdSettingsAsync(
        HouseholdSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The household settings row is missing although setup is complete.");

        return Results.Ok(HouseholdSettingsResponse.From(settings));
    }

    private static async Task<IResult> PatchHouseholdSettingsAsync(
        HttpContext context,
        HouseholdSettingsService settingsService,
        CancellationToken cancellationToken)
    {
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
}
