using FateAndFridge.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Path-matching rules for pre-setup gating (docs/API.md §21). These need no
/// database: gating is a pure function of the request path.
/// </summary>
public sealed class SetupGatingTests
{
    [Theory]
    [InlineData("/api/v1")]
    [InlineData("/api/v1/recipes")]
    [InlineData("/api/v1/recipes/42")]
    [InlineData("/api/v1/settings/household")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/users/1/permissions")]
    [InlineData("/api/v1/groceries")]
    [InlineData("/api/v1/healthiness")]
    [InlineData("/api/v1/setup-wizard")]
    [InlineData("/API/V1/Recipes")]
    public void Api_v1_paths_outside_health_and_setup_are_gated(string path) =>
        SetupGating.ShouldGate(new PathString(path)).Should().BeTrue();

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/api/v1/health/db")]
    [InlineData("/api/v1/health/")]
    [InlineData("/API/V1/HEALTH/DB")]
    [InlineData("/api/v1/setup")]
    [InlineData("/api/v1/setup/status")]
    [InlineData("/api/v1/setup/complete")]
    public void Health_and_setup_paths_are_exempt(string path) =>
        SetupGating.ShouldGate(new PathString(path)).Should().BeFalse();

    [Theory]
    [InlineData("/")]
    [InlineData("/calendar")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/api")]
    [InlineData("/apiv1/recipes")]
    [InlineData("/ws")]
    public void Non_api_v1_paths_are_never_gated(string path) =>
        SetupGating.ShouldGate(new PathString(path)).Should().BeFalse();
}
