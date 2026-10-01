using FateAndFridge.Api.Auth.Permissions;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Test-only permission probe routes (M1-09). The permission-matrix tests call
/// one probe per key to observe server-side enforcement without mutating real
/// data. The routes are mapped only in the <c>Testing</c> environment (see
/// Program.cs) and are ordinary <c>/api/v1</c> routes, so setup gating and the
/// forced-change gate apply to them like any other route, and they return no
/// secrets.
/// </summary>
public static class PermissionProbeEndpoints
{
    public sealed record PermissionProbeResponse(string Key, bool Authorized);

    public static IEndpointRouteBuilder MapPermissionProbeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/permission-probe").WithTags("Testing");

        foreach (var permissionKey in PermissionKeys.All)
        {
            group.MapGet(
                    $"/{permissionKey}",
                    () => Results.Ok(new PermissionProbeResponse(permissionKey, Authorized: true)))
                .RequirePermission(permissionKey);
        }

        // The same requirement kinds the real routes use, so the matrix tests
        // can cover them without touching settings or users.
        group.MapGet(
                "/any-authenticated",
                () => Results.Ok(new PermissionProbeResponse("authenticated", Authorized: true)))
            .RequireAuthenticated();

        group.MapGet(
                "/admin",
                () => Results.Ok(new PermissionProbeResponse("admin", Authorized: true)))
            .RequireAdmin();

        // Chained requirements prove AND semantics in both metadata orders.
        group.MapGet(
                "/chained/admin-then-permission",
                () => Results.Ok(new PermissionProbeResponse("admin+calendar.view", Authorized: true)))
            .RequireAdmin()
            .RequirePermission(PermissionKeys.CalendarView);

        group.MapGet(
                "/chained/permission-then-admin",
                () => Results.Ok(new PermissionProbeResponse("calendar.view+admin", Authorized: true)))
            .RequirePermission(PermissionKeys.CalendarView)
            .RequireAdmin();

        // Intentionally misconfigured: the enforcing filter is attached without
        // a PermissionRequirement so tests can prove it fails fast (server
        // misconfiguration) instead of silently allowing the request.
        group.MapGet(
                "/misconfigured",
                () => Results.Ok(new PermissionProbeResponse("misconfigured", Authorized: true)))
            .AddEndpointFilter<PermissionEndpointFilter>();

        // Deliberately failing route (M1-15): proves the exception-handling
        // middleware maps unexpected exceptions to the shared 500 envelope.
        group.MapGet("/throw", (HttpContext _) =>
            throw new InvalidOperationException("Test-only probe failure."));

        return app;
    }
}
