using FateAndFridge.Api.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Route-builder enforcement helpers: invalid permission keys fail
/// fast at mapping time, so a typo cannot silently create an unprotected or
/// permanently forbidden route, and the enforcing filter fails closed when it
/// is attached without any requirement metadata.
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

    [Fact]
    public async Task Filter_without_requirement_metadata_fails_closed()
    {
        // Attaching the filter without a PermissionRequirement is a server
        // misconfiguration: it must throw instead of failing open. The shared
        // exception handler maps it to the 500 envelope.
        var httpContext = new DefaultHttpContext();
        httpContext.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            EndpointMetadataCollection.Empty,
            "misconfigured"));

        var filter = new PermissionEndpointFilter(actorProvider: null!, permissionService: null!);
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var act = async () => await filter.InvokeAsync(
            context,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PermissionRequirement*");
    }
}
