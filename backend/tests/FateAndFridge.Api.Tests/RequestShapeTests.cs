using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Endpoints;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Request-shape hardening (issue #19): JSON body size cap and
/// Content-Type policy, plus the last-resort 500 envelope.
/// </summary>
public sealed class JsonBodyReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accepts_json_with_charset_and_a_missing_content_type()
    {
        (await ReadAsync("{\"a\":1}", "application/json; charset=utf-8")).IsSuccess.Should().BeTrue();
        (await ReadAsync("{\"a\":1}", contentType: null)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_other_media_types_with_415()
    {
        var result = await ReadAsync("{}", "text/plain");

        result.IsSuccess.Should().BeFalse();
        var (status, body) = await ExecuteAsync(result.Error!);
        status.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        body.Should().Contain(ErrorCodes.UnsupportedMediaType);
    }

    [Fact]
    public async Task Rejects_bodies_over_the_cap_by_declared_length_and_by_actual_read()
    {
        var context = CreateContext(new string(' ', JsonBodyReader.DefaultMaxBytes + 1), "application/json");
        var result = await JsonBodyReader.ReadAsync(context, cancellationToken: Ct);
        result.IsSuccess.Should().BeFalse();
        var (status, body) = await ExecuteAsync(result.Error!);
        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain(ErrorCodes.ValidationFailed);
        body.Should().Contain("at most");

        // A lying/absent Content-Length cannot bypass the cap.
        var chunked = CreateContext(new string(' ', JsonBodyReader.DefaultMaxBytes + 2048), "application/json");
        chunked.Request.ContentLength = null;
        var chunkedResult = await JsonBodyReader.ReadAsync(chunked, cancellationToken: Ct);
        chunkedResult.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_malformed_json_with_validation_failed()
    {
        var result = await ReadAsync("{not json", "application/json");

        result.IsSuccess.Should().BeFalse();
        var (status, body) = await ExecuteAsync(result.Error!);
        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain(ErrorCodes.ValidationFailed);
    }

    private static async Task<JsonBodyReadResult> ReadAsync(string body, string? contentType)
    {
        var context = CreateContext(body, contentType);
        return await JsonBodyReader.ReadAsync(context, cancellationToken: Ct);
    }

    private static DefaultHttpContext CreateContext(string body, string? contentType)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.ContentLength = bytes.Length;
        if (contentType is not null)
        {
            context.Request.ContentType = contentType;
        }

        context.Request.Body = new MemoryStream(bytes);
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(Ct);
        return (context.Response.StatusCode, body);
    }
}

/// <summary>HTTP-pipeline coverage for the request-shape and 500 envelope items.</summary>
[Collection(SetupCollection.Name)]
public sealed class RequestShapeIntegrationTests(SetupFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Json_endpoint_with_a_non_json_content_type_returns_415()
    {
        using var factory = await fixture.CreateConfiguredFactoryAsync(Ct);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };
        var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.UnsupportedMediaType);
    }

    [Fact]
    public async Task Missing_content_type_stays_accepted_for_compatibility()
    {
        using var factory = await fixture.CreateConfiguredFactoryAsync(Ct);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
        };
        request.Content.Headers.ContentType = null;
        var response = await client.SendAsync(request, Ct);

        // The body reaches validation (400 VALIDATION_FAILED), not 415.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Unexpected_exceptions_become_the_shared_500_envelope_without_leakage()
    {
        // A real product route whose authorization seam throws proves the
        // last-resort handler in the full HTTP pipeline. Replacing the actor
        // provider is a test-only DI seam; no product route or probe is added.
        using var configuredFactory = await fixture.CreateConfiguredFactoryAsync(Ct);
        using var factory = configuredFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICurrentActorProvider>();
                services.AddSingleton<ICurrentActorProvider, ThrowingActorProvider>();
            }));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/recipes", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var error = JsonDocument.Parse(raw).RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.InternalError);
        raw.Should().NotContain("InvalidOperationException");
        raw.Should().NotContain("boom: secret detail");
        raw.Should().NotContain("   at ");
    }

    private sealed class ThrowingActorProvider : ICurrentActorProvider
    {
        public Task<CurrentActor?> GetCurrentActorAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom: secret detail");
    }

    private static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("error");
    }
}
