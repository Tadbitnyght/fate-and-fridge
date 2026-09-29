using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task Health_Returns_Application_Identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        payload.GetProperty("status").GetString().Should().Be("ok");
        payload.GetProperty("app").GetString().Should().Be("Fate & Fridge");
        payload.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        payload.GetProperty("environment").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Root_Returns_Json_Index_When_Frontend_Is_Not_Built()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        payload.GetProperty("app").GetString().Should().Be("Fate & Fridge");
        payload.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
    }
}
