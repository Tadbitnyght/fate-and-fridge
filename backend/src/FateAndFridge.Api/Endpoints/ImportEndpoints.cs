using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Imports;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Import routes
/// <c>POST /api/v1/import/recipe-url</c> (draft-only, <c>recipes.edit</c>) and
/// <c>POST /api/v1/import/check</c> (<c>recipes.view</c>). Both use the shared
/// error envelope; the draft route throttles per-user page fetches.
/// </summary>
public static class ImportEndpoints
{
    private const string AuthRequiredMessage = "Authentication is required.";

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/import").WithTags("Import");

        group.MapPost("/recipe-url", ImportRecipeUrlAsync).RequirePermission(PermissionKeys.RecipesEdit);
        group.MapPost("/check", CheckAsync).RequirePermission(PermissionKeys.RecipesView);

        return app;
    }

    private static async Task<IResult> ImportRecipeUrlAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        PermissionService permissions,
        RecipeImportService import,
        IImportThrottle throttle,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!root.TryGetProperty("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(urlElement.GetString()))
        {
            return ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["url"] = ["url is required and must be a non-blank string."],
            });
        }

        var url = urlElement.GetString()!.Trim();

        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            ResponseCache.NoStore(context);
            return ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                AuthRequiredMessage);
        }

        var actorKey = actor.IsSystem
            ? "system"
            : actor.UserId?.ToString(CultureInfo.InvariantCulture) ?? "system";

        var decision = throttle.Reserve(actorKey, ImportFetchBudget.Page);
        if (!decision.Allowed)
        {
            context.Response.Headers.RetryAfter = Math
                .Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            return ApiResults.Error(
                StatusCodes.Status429TooManyRequests,
                ErrorCodes.ImportRateLimited,
                "Too many import fetches; try again later.");
        }

        var canCreateReferenceData = await permissions.HasPermissionAsync(
            actor,
            PermissionKeys.ReferenceDataEdit,
            cancellationToken);

        var result = await import.ImportAsync(url, canCreateReferenceData, cancellationToken);
        return result switch
        {
            ImportRecipeUrlResult.Success success => Results.Ok(success.Draft),
            ImportRecipeUrlResult.FetchFailed failed => MapFetchFailure(failed.Reason),
            _ => ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                ErrorCodes.ImportNoRecipe,
                "No recipe could be parsed from that page."),
        };
    }

    private static async Task<IResult> CheckAsync(
        HttpContext context,
        RecipeImportService import,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        string? sourceUrl = null;
        if (root.TryGetProperty("source_url", out var sourceUrlElement)
            && sourceUrlElement.ValueKind != JsonValueKind.Null)
        {
            if (sourceUrlElement.ValueKind != JsonValueKind.String)
            {
                errors["source_url"] = ["source_url must be a string or null."];
            }
            else
            {
                sourceUrl = sourceUrlElement.GetString();
            }
        }

        string? name = null;
        if (root.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind != JsonValueKind.Null)
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                errors["name"] = ["name must be a string or null."];
            }
            else
            {
                name = nameElement.GetString();
            }
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var result = await import.CheckAsync(sourceUrl, name, cancellationToken);
        return Results.Ok(new ImportDraftCheckResponse(result.SourceUrlMatch, result.NameMatches));
    }

    private static IResult MapFetchFailure(ImportFetchFailureReason reason) => reason switch
    {
        ImportFetchFailureReason.InvalidUrl => ApiResults.Error(
            StatusCodes.Status400BadRequest,
            ErrorCodes.ImportUrlInvalid,
            "The URL is not a valid http or https URL."),
        ImportFetchFailureReason.BlockedTarget => ApiResults.Error(
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.ImportUrlBlocked,
            "The URL targets a blocked address."),
        ImportFetchFailureReason.TooLarge => ApiResults.Error(
            StatusCodes.Status413PayloadTooLarge,
            ErrorCodes.ImportTooLarge,
            "The page exceeds the import size limit."),
        ImportFetchFailureReason.UnsupportedContentType => ApiResults.Error(
            StatusCodes.Status415UnsupportedMediaType,
            ErrorCodes.ImportUnsupportedContent,
            "The URL did not return HTML."),
        _ => ApiResults.Error(
            StatusCodes.Status502BadGateway,
            ErrorCodes.ImportFetchFailed,
            "The page could not be fetched."),
    };

    private static async Task<(JsonElement Root, IResult? Error)> ReadObjectAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(
            context,
            JsonBodyReader.DefaultMaxBytes,
            cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return (default, body.Error);
        }

        using var document = body.Document!;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (default, BodyError());
        }

        return (document.RootElement.Clone(), null);
    }

    private static IResult BodyError() =>
        ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body must be a valid JSON object."],
        });
}
