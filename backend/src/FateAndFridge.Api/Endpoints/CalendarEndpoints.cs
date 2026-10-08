using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Shared calendar date formatting for the API (ISO <c>YYYY-MM-DD</c>).</summary>
internal static class CalendarDateText
{
    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Calendar entry document (docs/M5_PLAN.md §2.4).</summary>
public sealed record CalendarEntryResponse(
    long Id,
    string EntryType,
    CalendarRecipeResponse? Recipe,
    string DisplayText,
    bool IsLocked,
    int? DinerCount,
    int EffectiveDinerCount,
    string? RestaurantName,
    string? LocationText,
    string? CustomLabel,
    string? Notes,
    string? IconKey,
    bool IncludeGroceries,
    bool BlocksAutomation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CalendarEntryResponse From(CalendarEntryRecord record) =>
        new(
            record.Id,
            record.EntryType,
            record.Recipe is null ? null : CalendarRecipeResponse.From(record.Recipe),
            record.DisplayText,
            record.IsLocked,
            record.DinerCount,
            record.EffectiveDinerCount,
            record.RestaurantName,
            record.LocationText,
            record.CustomLabel,
            record.Notes,
            record.IconKey,
            record.IncludeGroceries,
            record.BlocksAutomation,
            record.CreatedAt,
            record.UpdatedAt);
}

/// <summary>Recipe summary embedded on a calendar entry.</summary>
public sealed record CalendarRecipeResponse(long Id, string Name, string? ImageUrl, string Status)
{
    public static CalendarRecipeResponse From(CalendarRecipeSummary summary) =>
        new(summary.Id, summary.Name, summary.ImageUrl, summary.Status);
}

/// <summary>Exact-recipe repeat warning.</summary>
public sealed record CalendarRepeatWarningResponse(
    long RecipeId,
    string WindowStart,
    string WindowEnd,
    IReadOnlyList<CalendarOccurrenceResponse> Occurrences)
{
    public static CalendarRepeatWarningResponse From(CalendarRepeatWarning warning) =>
        new(
            warning.RecipeId,
            CalendarDateText.Format(warning.WindowStart),
            CalendarDateText.Format(warning.WindowEnd),
            warning.Occurrences.Select(CalendarOccurrenceResponse.From).ToArray());
}

/// <summary>One occurrence inside a repeat window.</summary>
public sealed record CalendarOccurrenceResponse(string Date, string EntryType)
{
    public static CalendarOccurrenceResponse From(CalendarOccurrence occurrence) =>
        new(CalendarDateText.Format(occurrence.Date), occurrence.EntryType);
}

/// <summary>One calendar date document.</summary>
public sealed record CalendarDayResponse(
    string Date,
    bool IsToday,
    bool IsHistorical,
    bool CanEdit,
    CalendarEntryResponse? Entry,
    CalendarRepeatWarningResponse? RepeatWarning)
{
    public static CalendarDayResponse From(CalendarDayRecord day) =>
        new(
            CalendarDateText.Format(day.Date),
            day.IsToday,
            day.IsHistorical,
            day.CanEdit,
            day.Entry is null ? null : CalendarEntryResponse.From(day.Entry),
            day.RepeatWarning is null ? null : CalendarRepeatWarningResponse.From(day.RepeatWarning));
}

/// <summary>Bounded range document.</summary>
public sealed record CalendarRangeResponse(
    string Start,
    string End,
    string Today,
    int WeekStartsOn,
    IReadOnlyList<CalendarDayResponse> Entries);

/// <summary>Repeat-check response.</summary>
public sealed record CalendarRepeatCheckResponse(
    bool IsRepeat,
    string WindowStart,
    string WindowEnd,
    IReadOnlyList<CalendarOccurrenceResponse> Occurrences);

/// <summary>Compact operation handle (docs/M6_PLAN.md §2.2).</summary>
public sealed record CalendarOperationResponse(string Id, DateTimeOffset? UndoExpiresAt);

/// <summary>Reroll response: the M5 day document plus the operation handle.</summary>
public sealed record CalendarRerollResponse(
    string Date,
    bool IsToday,
    bool IsHistorical,
    bool CanEdit,
    CalendarEntryResponse? Entry,
    CalendarRepeatWarningResponse? RepeatWarning,
    CalendarOperationResponse Operation);

/// <summary>
/// M5 calendar routes (docs/M5_PLAN.md §2.4): reads gated by
/// <c>calendar.view</c>, writes by <c>calendar.edit</c>, with server-enforced
/// historical immutability and range/horizon caps.
/// </summary>
public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/calendar").WithTags("Calendar");

        group.MapGet("", GetRangeAsync).RequirePermission(PermissionKeys.CalendarView);
        group.MapGet("/{date}", GetDayAsync).RequirePermission(PermissionKeys.CalendarView);
        group.MapGet("/{date}/repeat-check", RepeatCheckAsync)
            .RequirePermission(PermissionKeys.CalendarView);
        group.MapPut("/{date}", UpsertAsync).RequirePermission(PermissionKeys.CalendarEdit);
        group.MapDelete("/{date}", DeleteAsync).RequirePermission(PermissionKeys.CalendarEdit);
        group.MapPost("/{date}/lock", LockAsync).RequirePermission(PermissionKeys.CalendarEdit);
        group.MapPost("/{date}/unlock", UnlockAsync).RequirePermission(PermissionKeys.CalendarEdit);
        group.MapPost("/{date}/reroll", RerollAsync).RequirePermission(PermissionKeys.CalendarEdit);

        return app;
    }

    private static async Task<IResult> GetRangeAsync(
        HttpContext context,
        HouseholdClock clock,
        CalendarService calendar,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var start = ReadQueryDate(context, "start", errors);
        var end = ReadQueryDate(context, "end", errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        if (start > end)
        {
            errors["end"] = ["end must be on or after start."];
        }

        if (end.DayNumber - start.DayNumber + 1 > HouseholdClock.MaxRangeDays)
        {
            errors["end"] = [$"The range may span at most {HouseholdClock.MaxRangeDays} days."];
        }

        var household = await clock.GetContextAsync(cancellationToken);
        ValidateHorizon(start, "start", household.Today, errors);
        ValidateHorizon(end, "end", household.Today, errors);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var range = await calendar.GetRangeAsync(start, end, cancellationToken);
        return Results.Ok(new CalendarRangeResponse(
            range.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            range.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            range.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            range.WeekStartsOn,
            range.Entries.Select(CalendarDayResponse.From).ToArray()));
    }

    private static async Task<IResult> GetDayAsync(
        string date,
        HouseholdClock clock,
        CalendarService calendar,
        CancellationToken cancellationToken)
    {
        if (!TryReadDate(date, out var parsedDate, out var error))
        {
            return error!;
        }

        var household = await clock.GetContextAsync(cancellationToken);
        var horizonErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateHorizon(parsedDate, "date", household.Today, horizonErrors);
        if (horizonErrors.Count > 0)
        {
            return ApiResults.ValidationError(horizonErrors);
        }

        var day = await calendar.GetDayAsync(parsedDate, cancellationToken);
        return Results.Ok(CalendarDayResponse.From(day));
    }

    private static async Task<IResult> RepeatCheckAsync(
        string date,
        HttpContext context,
        HouseholdClock clock,
        CalendarService calendar,
        CancellationToken cancellationToken)
    {
        if (!TryReadDate(date, out var parsedDate, out var error))
        {
            return error!;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        long? recipeId = null;
        if (!context.Request.Query.TryGetValue("recipe_id", out var values)
            || values.Count == 0
            || !long.TryParse(values[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId)
            || parsedId <= 0)
        {
            errors["recipe_id"] = ["recipe_id is required and must be a positive integer."];
        }
        else
        {
            recipeId = parsedId;
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var household = await clock.GetContextAsync(cancellationToken);
        ValidateHorizon(parsedDate, "date", household.Today, errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var check = await calendar.RepeatCheckAsync(parsedDate, recipeId!.Value, cancellationToken);
        return Results.Ok(new CalendarRepeatCheckResponse(
            check.IsRepeat,
            check.WindowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            check.WindowEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            check.Occurrences.Select(CalendarOccurrenceResponse.From).ToArray()));
    }

    private static async Task<IResult> UpsertAsync(
        string date,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        HouseholdClock clock,
        CalendarService calendar,
        CancellationToken cancellationToken)
    {
        if (!TryReadDate(date, out var parsedDate, out var error))
        {
            return error!;
        }

        var (root, bodyError) = await ReadObjectAsync(context, cancellationToken);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var request = ParseWriteRequest(root, errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var household = await clock.GetContextAsync(cancellationToken);
        ValidateHorizon(parsedDate, "date", household.Today, errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, actorError) = await ResolveActorAsync(actorProvider, cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await calendar.UpsertAsync(parsedDate, request, actorUserId, cancellationToken);
        return Map(result);
    }

    private static async Task<IResult> DeleteAsync(
        string date,
        HouseholdClock clock,
        ICurrentActorProvider actorProvider,
        CalendarService calendar,
        CancellationToken cancellationToken)
    {
        if (!TryReadDate(date, out var parsedDate, out var error))
        {
            return error!;
        }

        var household = await clock.GetContextAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateHorizon(parsedDate, "date", household.Today, errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, actorError) = await ResolveActorAsync(actorProvider, cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await calendar.DeleteAsync(parsedDate, actorUserId, cancellationToken);
        return result is CalendarMutationResult.Success ? Results.NoContent() : Map(result);
    }

    private static Task<IResult> LockAsync(
        string date,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        HouseholdClock clock,
        CalendarService calendar,
        CancellationToken cancellationToken) =>
        SetLockAsync(date, locked: true, actorProvider, clock, calendar, cancellationToken);

    private static Task<IResult> UnlockAsync(
        string date,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        HouseholdClock clock,
        CalendarService calendar,
        CancellationToken cancellationToken) =>
        SetLockAsync(date, locked: false, actorProvider, clock, calendar, cancellationToken);

    private static async Task<IResult> SetLockAsync(
        string date,
        bool locked,
        ICurrentActorProvider actorProvider,
        HouseholdClock clock,
        CalendarService calendar,
        CancellationToken cancellationToken)
    {
        if (!TryReadDate(date, out var parsedDate, out var error))
        {
            return error!;
        }

        var household = await clock.GetContextAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateHorizon(parsedDate, "date", household.Today, errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, actorError) = await ResolveActorAsync(actorProvider, cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await calendar.SetLockAsync(parsedDate, locked, actorUserId, cancellationToken);
        return Map(result);
    }

    private static async Task<IResult> RerollAsync(
        string date,
        ICurrentActorProvider actorProvider,
        HouseholdClock clock,
        CalendarRandomizer randomizer,
        CancellationToken cancellationToken)
    {
        if (!TryReadDate(date, out var parsedDate, out var error))
        {
            return error!;
        }

        var household = await clock.GetContextAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateHorizon(parsedDate, "date", household.Today, errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, actorError) = await ResolveActorAsync(actorProvider, cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await randomizer.RerollAsync(parsedDate, actorUserId, cancellationToken);
        return result switch
        {
            CalendarRerollOutcome.Success success => Results.Ok(new CalendarRerollResponse(
                CalendarDateText.Format(success.Day.Date),
                success.Day.IsToday,
                success.Day.IsHistorical,
                success.Day.CanEdit,
                success.Day.Entry is null ? null : CalendarEntryResponse.From(success.Day.Entry),
                success.Day.RepeatWarning is null
                    ? null
                    : CalendarRepeatWarningResponse.From(success.Day.RepeatWarning),
                new CalendarOperationResponse(success.OperationId.ToString(), UndoExpiresAt: null))),
            CalendarRerollOutcome.Failure failure => ApiResults.Error(
                failure.StatusCode,
                failure.Code,
                failure.Message),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static CalendarWriteRequest ParseWriteRequest(
        JsonElement root,
        Dictionary<string, string[]> errors)
    {
        var entryType = ReadOptionalString(root, "entry_type", errors);
        var recipeId = ReadOptionalLong(root, "recipe_id", errors);
        var dinerCount = ReadOptionalInt(root, "diner_count", errors);
        var restaurantName = ReadOptionalString(root, "restaurant_name", errors);
        var locationText = ReadOptionalString(root, "location_text", errors);
        var customLabel = ReadOptionalString(root, "custom_label", errors);
        var notes = ReadOptionalString(root, "notes", errors);
        var iconKey = ReadOptionalString(root, "icon_key", errors);

        return new CalendarWriteRequest(
            entryType,
            recipeId,
            dinerCount,
            restaurantName,
            locationText,
            customLabel,
            notes,
            iconKey);
    }

    private static string? ReadOptionalString(
        JsonElement root,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            _ => AddTypeError(errors, field, "a string or null"),
        };
    }

    private static long? ReadOptionalLong(
        JsonElement root,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
        {
            return value;
        }

        AddTypeError(errors, field, "an integer or null");
        return null;
    }

    private static int? ReadOptionalInt(
        JsonElement root,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        AddTypeError(errors, field, "an integer or null");
        return null;
    }

    private static string? AddTypeError(
        Dictionary<string, string[]> errors,
        string field,
        string expected)
    {
        errors[field] = [$"{field} must be {expected}."];
        return null;
    }

    private static IResult Map(CalendarMutationResult result) => result switch
    {
        CalendarMutationResult.Success success => Results.Ok(CalendarDayResponse.From(success.Day)),
        CalendarMutationResult.ValidationFailed failed => failed.Code is null
            ? ApiResults.ValidationError(failed.FieldErrors)
            : ApiResults.Error(
                StatusCodes.Status400BadRequest,
                failed.Code,
                "The request failed validation.",
                new Dictionary<string, object?> { ["field_errors"] = failed.FieldErrors }),
        CalendarMutationResult.Failure failure => ApiResults.Error(
            failure.StatusCode,
            failure.Code,
            failure.Message),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
    };

    internal static async Task<(long? ActorUserId, IResult? Error)> ResolveActorAsync(
        ICurrentActorProvider actorProvider,
        CancellationToken cancellationToken)
    {
        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            return (null, ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                "Authentication is required."));
        }

        return (actor.IsSystem ? null : actor.UserId, null);
    }

    private static DateOnly ReadQueryDate(
        HttpContext context,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (!context.Request.Query.TryGetValue(field, out var values)
            || values.Count == 0
            || !TryParseDate(values[^1], out var date))
        {
            errors[field] = [$"{field} is required and must be a YYYY-MM-DD date."];
            return default;
        }

        return date;
    }

    private static bool TryReadDate(string raw, out DateOnly date, out IResult? error)
    {
        if (TryParseDate(raw, out date))
        {
            error = null;
            return true;
        }

        error = ApiResults.Error(
            StatusCodes.Status400BadRequest,
            ErrorCodes.ValidationFailed,
            "The date must be a valid YYYY-MM-DD date.");
        return false;
    }

    private static bool TryParseDate(string? raw, out DateOnly date) =>
        DateOnly.TryParseExact(
            raw,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static void ValidateHorizon(
        DateOnly date,
        string field,
        DateOnly today,
        Dictionary<string, string[]> errors)
    {
        var earliest = today.AddYears(-HouseholdClock.HorizonYears);
        var latest = today.AddYears(HouseholdClock.HorizonYears);
        if (date < earliest || date > latest)
        {
            errors[field] =
                [$"{field} must be within {HouseholdClock.HorizonYears} years of today."];
        }
    }

    private static async Task<(JsonElement Root, IResult? Error)> ReadObjectAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return (default, body.Error);
        }

        using var document = body.Document!;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (default, ApiResults.ValidationError(new Dictionary<string, string[]>
            {
                ["body"] = ["The request body must be a valid JSON object."],
            }));
        }

        return (document.RootElement.Clone(), null);
    }
}
