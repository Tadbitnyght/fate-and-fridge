namespace FateAndFridge.Api.LiveSync;

/// <summary>
/// Shared path constants for the live-sync hub so the endpoint mapping, the CSRF
/// exemption, and the setup/forced-password gates cannot drift apart.
/// </summary>
public static class LiveSyncPaths
{
    /// <summary>The hub route (docs/M9_PLAN.md §1.2), mapped before the SPA fallback.</summary>
    public const string HubPath = "/hub/live";

    private static readonly PathString Hub = new(HubPath);

    /// <summary>True for the hub route and its subpaths (for example <c>/negotiate</c>).</summary>
    public static bool IsHubPath(PathString path) => path.StartsWithSegments(Hub);
}
