using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Update status routes (docs/M13_PLAN.md §1.13–§1.14, §2.2; docs/API.md §25).
/// </summary>
/// <remarks>
/// The status read is for every authenticated actor — the About section and the
/// dashboard banner are visible to members too — while the forced check and the
/// enabled preference stay Admin-only on the existing role. No new permission
/// key is introduced and nothing here updates the application.
/// </remarks>
public static class UpdateEndpoints
{
    private const string StatusPath = "/api/v1/updates";

    private const string AdminPath = "/api/v1/admin/updates";

    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(StatusPath, GetStatusAsync)
            .WithTags("Settings")
            .RequireAuthenticated();

        app.MapPost($"{AdminPath}/check", CheckAsync)
            .WithTags("Admin")
            .RequireAdmin();

        app.MapPatch(AdminPath, PatchAsync)
            .WithTags("Admin")
            .RequireAdmin();

        return app;
    }

    /// <summary>The cached status; never triggers an outbound request.</summary>
    private static async Task<IResult> GetStatusAsync(
        UpdateCheckService updateCheck,
        CancellationToken cancellationToken) =>
        Results.Ok(await updateCheck.GetStatusAsync(cancellationToken));

    /// <summary>
    /// Forces one check even inside the 24-hour cache. A feed failure is a
    /// recorded <c>last_error</c>, not an HTTP error: the route answers
    /// <c>200</c> with the status either way.
    /// </summary>
    private static async Task<IResult> CheckAsync(
        UpdateCheckService updateCheck,
        CancellationToken cancellationToken) =>
        Results.Ok(await updateCheck.CheckAsync(force: true, cancellationToken));

    /// <summary>
    /// Sets <c>update_check_enabled</c> (the setup preference) and audits the
    /// change. Only the boolean is accepted; the response is the fresh status.
    /// </summary>
    private static async Task<IResult> PatchAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        UpdateCheckService updateCheck,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        bool enabled;
        using (var document = body.Document!)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("enabled", out var enabledElement)
                || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return ApiResults.ValidationError(new Dictionary<string, string[]>
                {
                    ["enabled"] = ["enabled must be a boolean."],
                });
            }

            enabled = enabledElement.GetBoolean();
        }

        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var status = await updateCheck.SetEnabledAsync(
                enabled,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(status);
    }
}
