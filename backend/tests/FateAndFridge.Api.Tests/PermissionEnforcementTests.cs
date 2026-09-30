using FateAndFridge.Api.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Route-builder enforcement helpers (M1-09): invalid permission keys fail
/// fast at mapping time, so a typo cannot silently create an unprotected or
/// permanently forbidden route.
/// </summary>
public sealed class PermissionEnforcementTests
{
    [Fact]
    public void RequirePermission_rejects_an_unknown_key_at_mapping_time()
    {
        var builder = new RouteHandlerBuilder([]);

        FluentActions.Invoking(() => builder.RequirePermission("bogus.key"))
            .Should().Throw<ArgumentException>()
            .WithParameterName("permissionKey");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequirePermission_rejects_blank_keys_at_mapping_time(string? permissionKey)
    {
        var builder = new RouteHandlerBuilder([]);

        FluentActions.Invoking(() => builder.RequirePermission(permissionKey!))
            .Should().Throw<ArgumentException>();
    }
}
