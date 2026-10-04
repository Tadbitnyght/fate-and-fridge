using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Imports;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the M4-07 image-from-URL route
/// <c>POST /api/v1/recipes/{id}/image/from-url</c> (docs/M4_PLAN.md
/// §1.8/§1.13–§1.15, §2.2; docs/SECURITY.md §6–§9): SSRF-hardened fetch,
/// M3 content policy on the actual bytes, the structured M4 error codes, the
/// per-actor image-fetch budget, the route permission matrix, and replacement
/// atomicity (a failed download never alters or loses the existing image and
/// leaves no partial file). Uses an injected fake <see cref="IImportFetcher"/>
/// and inline fixtures; no test touches the public internet. Each test uses its
/// own temporary data directory so stored files are isolated.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class ImportImageEndpointIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string RecipesPath = "/api/v1/recipes";
    private const string SourceUrl = "https://example.com/lasagna.jpg";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "import-image-endpoints-test";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "faf-import-images-" + Guid.NewGuid().ToString("N"));

    private string ImagesRoot => Path.Combine(_dataDir, "images", "recipes");

    private static string FromUrlPath(long recipeId) => $"{RecipesPath}/{recipeId}/image/from-url";

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        // M1SetupFixture.ResetAsync already clears the M3 recipe tables; the
        // reset is sufficient for this suite.
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover per-test temp directory is harmless; cleanup must not
            // fail the test run.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Download_replaces_the_active_image_and_records_the_source_url()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(320, 240)));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Download Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var bytes = Jpeg(320, 240);

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var image = await ReadJsonAsync(response, cancellationToken);
        image.GetProperty("id").GetInt64().Should().BeGreaterThan(0);
        image.GetProperty("source_url").GetString().Should().Be(SourceUrl);
        image.GetProperty("alt_text").ValueKind.Should().Be(JsonValueKind.Null);
        image.GetProperty("url").GetString().Should().Be($"{RecipesPath}/{recipe.Id}/image");

        var stored = await ReadStoredImageAsync(recipe.Id, cancellationToken);
        stored.Should().NotBeNull();
        stored!.SourceUrl.Should().Be(SourceUrl);
        stored.Id.Should().Be(image.GetProperty("id").GetInt64());

        var files = ReadImageFiles();
        files.Should().ContainSingle();
        Path.GetFileName(files[0]).Should().MatchRegex("^[0-9a-f]{32}\\.jpg$");
        (await File.ReadAllBytesAsync(files[0], cancellationToken)).Should().Equal(bytes);

        var served = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}/image", cancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await served.Content.ReadAsByteArrayAsync(cancellationToken)).Should().Equal(bytes);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        var row = rows.Should().ContainSingle().Which;
        row.EventType.Should().Be(AuditEventTypes.ImageUpdated);
        row.EntityType.Should().Be(AuditEntityTypes.Recipe);
        row.EntityId.Should().Be(recipe.Id.ToString(CultureInfo.InvariantCulture));
        row.ActorUserId.Should().Be(admin.Id);
        using var after = JsonDocument.Parse(row.AfterJson!);
        after.RootElement.GetProperty("source_url").GetString().Should().Be(SourceUrl);
        after.RootElement.GetProperty("recipe_id").GetInt64().Should().Be(recipe.Id);
    }

    [Fact]
    public async Task Download_records_the_final_post_redirect_source_url()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        const string requestedUrl = "https://example.test/redirected-image";
        const string finalUrl = "https://cdn.example.test/final-image.jpg";
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(finalUrl), 200, "image/jpeg", Jpeg(64, 64)));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Redirect Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{requestedUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var image = await ReadJsonAsync(response, cancellationToken);
        image.GetProperty("source_url").GetString().Should().Be(
            finalUrl,
            "the stored source_url must be the final post-redirect URL");

        var stored = await ReadStoredImageAsync(recipe.Id, cancellationToken);
        stored.Should().NotBeNull();
        stored!.SourceUrl.Should().Be(finalUrl);
    }

    [Fact]
    public async Task Download_replacing_an_existing_image_leaves_exactly_one_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(320, 240)));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Replace Twice", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        for (var i = 0; i < 2; i++)
        {
            var response = await SendJsonAsync(
                session.Client,
                FromUrlPath(recipe.Id),
                $$"""{"source_url":"{{SourceUrl}}"}""",
                session.CsrfToken,
                cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The old file must be removed by the atomic replacement: one row, one file.
        ReadImageFiles().Should().ContainSingle();
        var stored = await ReadStoredImageAsync(recipe.Id, cancellationToken);
        stored.Should().NotBeNull();
        ReadImageFiles()[0].Should().Be(stored!.StoragePath);
    }

    [Fact]
    public async Task Download_supports_png_and_webp_actual_bytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        var formats = new (string Label, byte[] Bytes, string ContentType, string Extension)[]
        {
            ("png", Png(120, 90), "image/png", "png"),
            ("webp", WebpLossy(120, 90), "image/webp", "webp"),
        };

        foreach (var format in formats)
        {
            var fetcher = new FakeImportFetcher().OnImage(_ =>
                ImportFetchResult.Success(new Uri(SourceUrl), 200, format.ContentType, format.Bytes));
            using var factory = CreateFactory(fetcher);
            var recipe = await SeedRecipeAsync($"Download {format.Label}", cancellationToken);
            using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

            var response = await SendJsonAsync(
                session.Client,
                FromUrlPath(recipe.Id),
                $$"""{"source_url":"{{SourceUrl}}"}""",
                session.CsrfToken,
                cancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var stored = await ReadStoredImageAsync(recipe.Id, cancellationToken);
            stored.Should().NotBeNull();
            Path.GetExtension(stored!.StoragePath).Should().Be($".{format.Extension}");
        }
    }

    [Theory]
    [InlineData(ImportFetchFailureReason.BlockedTarget, HttpStatusCode.UnprocessableEntity, ErrorCodes.ImportUrlBlocked)]
    [InlineData(ImportFetchFailureReason.InvalidUrl, HttpStatusCode.BadRequest, ErrorCodes.ImportUrlInvalid)]
    [InlineData(ImportFetchFailureReason.TooLarge, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.ImportTooLarge)]
    [InlineData(ImportFetchFailureReason.UnsupportedContentType, HttpStatusCode.UnsupportedMediaType, ErrorCodes.ImportUnsupportedContent)]
    [InlineData(ImportFetchFailureReason.TooManyRedirects, HttpStatusCode.BadGateway, ErrorCodes.ImportFetchFailed)]
    [InlineData(ImportFetchFailureReason.Timeout, HttpStatusCode.BadGateway, ErrorCodes.ImportFetchFailed)]
    [InlineData(ImportFetchFailureReason.HttpError, HttpStatusCode.BadGateway, ErrorCodes.ImportFetchFailed)]
    [InlineData(ImportFetchFailureReason.DnsFailure, HttpStatusCode.BadGateway, ErrorCodes.ImportFetchFailed)]
    public async Task Download_maps_fetch_failures_to_m4_codes(
        ImportFetchFailureReason reason,
        HttpStatusCode status,
        string code)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnImage(_ => ImportFetchResult.Failure(reason));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Failure Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(status);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString().Should().Be(code);
        ReadImageFiles().Should().BeEmpty("a failed download writes no file");
        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty("a failed download writes no audit row");
    }

    [Fact]
    public async Task Download_rejects_a_content_type_mismatch_with_unsupported_content()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        // PNG bytes, but the server lied and said JPEG.
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Png(64, 64)));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Mismatch Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportUnsupportedContent);
        ReadImageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Download_rejects_svg_html_gif_and_truncated_bytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        var payloads = new (string Label, byte[] Bytes, string DeclaredType)[]
        {
            ("svg", Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"), "image/svg+xml"),
            ("html", Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>hi</body></html>"), "text/html"),
            ("gif", Encoding.ASCII.GetBytes("GIF89a"), "image/gif"),
            ("truncated-png", Png(64, 64)[..20], "image/png"),
        };

        foreach (var (label, bytes, declaredType) in payloads)
        {
            var fetcher = new FakeImportFetcher().OnImage(_ =>
                ImportFetchResult.Success(new Uri(SourceUrl), 200, declaredType, bytes));
            using var factory = CreateFactory(fetcher);
            var recipe = await SeedRecipeAsync($"Reject {label}", cancellationToken);
            using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

            var response = await SendJsonAsync(
                session.Client,
                FromUrlPath(recipe.Id),
                $$"""{"source_url":"{{SourceUrl}}"}""",
                session.CsrfToken,
                cancellationToken);

            response.StatusCode.Should().Be(
                HttpStatusCode.UnsupportedMediaType,
                $"{label} is not a supported still raster image");
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.ImportUnsupportedContent);
            ReadImageFiles().Should().BeEmpty($"{label} leaves no file");
        }
    }

    [Fact]
    public async Task Download_rejects_bytes_over_five_mib_with_too_large()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        // A structurally valid PNG header padded past the M3 5 MiB cap: the
        // service's own size re-check must reject it even though the (faked)
        // fetch did not.
        var oversized = Png(64, 64);
        Array.Resize(ref oversized, RecipeImageService.MaxFileBytes + 1);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/png", oversized));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Oversize Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportTooLarge);
        ReadImageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Download_rejects_images_over_the_dimension_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        var payloads = new (string Label, byte[] Bytes, string ContentType)[]
        {
            ("wide-png", Png(RecipeImageService.MaxDimension + 1, 10), "image/png"),
            ("tall-jpeg", Jpeg(10, RecipeImageService.MaxDimension + 1), "image/jpeg"),
        };

        foreach (var (label, bytes, contentType) in payloads)
        {
            var fetcher = new FakeImportFetcher().OnImage(_ =>
                ImportFetchResult.Success(new Uri(SourceUrl), 200, contentType, bytes));
            using var factory = CreateFactory(fetcher);
            var recipe = await SeedRecipeAsync($"Dimension {label}", cancellationToken);
            using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

            var response = await SendJsonAsync(
                session.Client,
                FromUrlPath(recipe.Id),
                $$"""{"source_url":"{{SourceUrl}}"}""",
                session.CsrfToken,
                cancellationToken);

            response.StatusCode.Should().Be(
                HttpStatusCode.UnsupportedMediaType,
                $"{label} exceeds the {RecipeImageService.MaxDimension}px limit");
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.ImportUnsupportedContent);
            ReadImageFiles().Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Download_rejects_a_missing_or_blank_source_url_with_validation_failed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        var recipe = await SeedRecipeAsync("Blank Url Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        foreach (var json in new[] { "{}", """{"source_url":""}""", """{"source_url":"   "}""", """{"source_url":42}""" })
        {
            var response = await SendJsonAsync(
                session.Client,
                FromUrlPath(recipe.Id),
                json,
                session.CsrfToken,
                cancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, json);
            var error = await ReadErrorAsync(response, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed, json);
            error.GetProperty("details").GetProperty("field_errors").TryGetProperty("source_url", out _)
                .Should().BeTrue(json);
        }
    }

    [Fact]
    public async Task Download_returns_not_found_for_a_missing_recipe_without_fetching()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(64, 64)));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(987654321),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);
        fetcher.ImageCallCount.Should().Be(0, "a missing recipe is rejected before any outbound fetch");
        ReadImageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Download_missing_recipe_does_not_consume_the_throttle_budget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(64, 64)));
        using var factory = CreateFactory(fetcher, clock, options => options.ImageFetchesPerHour = 1);
        var recipe = await SeedRecipeAsync("Budget Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        // Burn several 404s against caller-chosen ids; they must not consume the budget.
        for (var i = 0; i < 3; i++)
        {
            var missing = await SendJsonAsync(
                session.Client,
                FromUrlPath(900000000 + i),
                $$"""{"source_url":"{{SourceUrl}}"}""",
                session.CsrfToken,
                cancellationToken);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        fetcher.ImageCallCount.Should().Be(0);

        var allowed = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        allowed.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a 404 for an unknown recipe must not consume the caller's image budget");
        fetcher.ImageCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Download_throttles_the_image_budget_before_fetching()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(64, 64)));
        using var factory = CreateFactory(fetcher, clock, options => options.ImageFetchesPerHour = 2);
        var recipe = await SeedRecipeAsync("Throttle Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        for (var i = 0; i < 2; i++)
        {
            var allowed = await SendJsonAsync(
                session.Client,
                FromUrlPath(recipe.Id),
                $$"""{"source_url":"{{SourceUrl}}"}""",
                session.CsrfToken,
                cancellationToken);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var callsBeforeBlocked = fetcher.ImageCallCount;
        var limited = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        (await ReadErrorAsync(limited, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportRateLimited);
        fetcher.ImageCallCount.Should().Be(
            callsBeforeBlocked,
            "the throttle must reject before any outbound fetch");

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        var recovered = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);
        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Download_throttles_the_single_user_actor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(64, 64)));
        using var factory = CreateFactory(fetcher, clock, options => options.ImageFetchesPerHour = 1);
        var recipe = await SeedRecipeAsync("System Throttle Target", cancellationToken);
        using var client = factory.CreateClient();

        var allowed = await SendJsonAsync(
            client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            csrfToken: null,
            cancellationToken);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);

        var callsBeforeBlocked = fetcher.ImageCallCount;
        var limited = await SendJsonAsync(
            client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            csrfToken: null,
            cancellationToken);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadErrorAsync(limited, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportRateLimited);
        fetcher.ImageCallCount.Should().Be(callsBeforeBlocked);
    }

    [Theory]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("recipes_edit", HttpStatusCode.OK)]
    [InlineData("recipes_view", HttpStatusCode.Forbidden)]
    [InlineData("none", HttpStatusCode.Forbidden)]
    public async Task Download_permission_matrix(string actor, HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(64, 64)));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Matrix Target", cancellationToken);

        HttpClient client;
        string? csrf;
        switch (actor)
        {
            case "admin":
                await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
                (client, csrf) = await SignInClientAsync(factory, "root", AdminPassword, cancellationToken);
                break;
            case "recipes_edit":
                var editor = await SeedUserAsync("editor", MemberPassword, cancellationToken);
                await SeedPermissionAsync(editor.Id, PermissionKeys.RecipesEdit, cancellationToken);
                (client, csrf) = await SignInClientAsync(factory, "editor", MemberPassword, cancellationToken);
                break;
            case "recipes_view":
                var viewer = await SeedUserAsync("viewer", MemberPassword, cancellationToken);
                await SeedPermissionAsync(viewer.Id, PermissionKeys.RecipesView, cancellationToken);
                (client, csrf) = await SignInClientAsync(factory, "viewer", MemberPassword, cancellationToken);
                break;
            default:
                await SeedUserAsync("nobody", MemberPassword, cancellationToken);
                (client, csrf) = await SignInClientAsync(factory, "nobody", MemberPassword, cancellationToken);
                break;
        }

        using (client)
        {
            var response = await SendJsonAsync(
                client,
                FromUrlPath(recipe.Id),
                $$"""{"source_url":"{{SourceUrl}}"}""",
                csrf,
                cancellationToken);

            response.StatusCode.Should().Be(expected);
            if (expected == HttpStatusCode.Forbidden)
            {
                (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                    .Should().Be(ErrorCodes.Forbidden);
                fetcher.ImageCallCount.Should().Be(0, "a denied actor triggers no outbound fetch");
            }
        }
    }

    [Fact]
    public async Task Download_denies_a_reference_data_editor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var actor = await SeedUserAsync("reference", MemberPassword, cancellationToken);
        await SeedPermissionAsync(actor.Id, PermissionKeys.ReferenceDataEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        var recipe = await SeedRecipeAsync("Reference Target", cancellationToken);
        using var session = await SignInAsync(factory, "reference", MemberPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Download_requires_authentication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(64, 64)));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Anonymous Target", cancellationToken);

        var response = await SendJsonAsync(
            factory.CreateClient(),
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            csrfToken: null,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);
        fetcher.ImageCallCount.Should().Be(0);
        ReadImageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Download_works_in_single_user_mode_without_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", Jpeg(64, 64)));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("System Image Target", cancellationToken);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            csrfToken: null,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response, cancellationToken)).GetProperty("source_url").GetString()
            .Should().Be(SourceUrl);
        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle();
        rows[0].ActorUserId.Should().BeNull("the single-user system actor has no user id");
    }

    [Fact]
    public async Task Failed_download_leaves_the_previous_image_intact_and_writes_no_partial_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher()
            .OnImage(_ => ImportFetchResult.Failure(ImportFetchFailureReason.BlockedTarget, "rebinding"));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Atomic Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var original = Png(100, 100);
        var originalId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);
        var originalFile = ReadImageFiles().Should().ContainSingle().Which;
        var auditBefore = (await ReadAuditRowsAsync(cancellationToken)).Count;

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportUrlBlocked);

        var stored = await ReadStoredImageAsync(recipe.Id, cancellationToken);
        stored.Should().NotBeNull("the previous image row survives a failed download");
        stored!.Id.Should().Be(originalId, "the previous image row survives a failed download");
        storageUnchanged(stored, originalFile);

        ReadImageFiles().Should().ContainSingle("no partial file is left on disk");
        ReadImageFiles()[0].Should().Be(originalFile);

        var served = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}/image", cancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync(cancellationToken)).Should()
            .Equal(original, "the previous image bytes are untouched");
        (await ReadAuditRowsAsync(cancellationToken)).Should()
            .HaveCount(auditBefore, "a failed download writes no audit row");

        static void storageUnchanged(RecipeImage stored, string expectedFile) =>
            stored.StoragePath.Should().Be(expectedFile);
    }

    [Fact]
    public async Task Rejected_content_leaves_the_previous_image_intact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(
                new Uri(SourceUrl),
                200,
                "image/svg+xml",
                Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Rejected Replace Target", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var original = Png(100, 100);
        var originalId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);
        var originalFile = ReadImageFiles().Should().ContainSingle().Which;
        var auditBefore = (await ReadAuditRowsAsync(cancellationToken)).Count;

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportUnsupportedContent);

        (await ReadStoredImageAsync(recipe.Id, cancellationToken))!.Id.Should().Be(originalId);
        ReadImageFiles().Should().ContainSingle().Which.Should().Be(originalFile);
        (await ReadAuditRowsAsync(cancellationToken)).Should().HaveCount(auditBefore);
    }

    private ApiFactory CreateFactory(
        FakeImportFetcher fetcher,
        TestTimeProvider? clock = null,
        Action<ImportThrottleOptions>? configureThrottle = null) =>
        fixture.CreateFactory(_dataDir, configureServices: services =>
        {
            services.RemoveAll<IImportFetcher>();
            services.AddSingleton<IImportFetcher>(fetcher);

            if (clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            }

            if (configureThrottle is not null)
            {
                services.Configure(configureThrottle);
            }
        });

    private string[] ReadImageFiles() =>
        Directory.Exists(ImagesRoot)
            ? Directory.GetFiles(ImagesRoot)
            : [];

    private async Task<long> UploadSeedImageAsync(
        SignedInClient session,
        long recipeId,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("image/png");
        content.Add(file, "file", "seed.png");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{RecipesPath}/{recipeId}/image")
        {
            Content = content,
        };
        request.Headers.Add(CsrfMiddleware.HeaderName, session.CsrfToken);

        var response = await session.Client.SendAsync(request, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seed image must upload for the atomicity test");
        return (await ReadJsonAsync(response, cancellationToken)).GetProperty("id").GetInt64();
    }

    private async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);

        var login = await client.SendAsync(request, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        return new SignedInClient(
            client,
            login.Headers.GetValues(CsrfMiddleware.HeaderName).Single());
    }

    private async Task<(HttpClient Client, string CsrfToken)> SignInClientAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var session = await SignInAsync(factory, username, password, cancellationToken);
        return (session.Client, session.CsrfToken);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        string path,
        string? json,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private async Task<Recipe> SeedRecipeAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = new Recipe
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            Status = RecipeStatus.Normal,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(cancellationToken);
        return recipe;
    }

    private async Task<RecipeImage?> ReadStoredImageAsync(long recipeId, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.RecipeImages.AsNoTracking()
            .SingleOrDefaultAsync(image => image.RecipeId == recipeId, cancellationToken);
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents.AsNoTracking()
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Import Image Endpoints Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkSetupCompletedAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }

    private async Task<User> SeedUserAsync(
        string username,
        string password,
        CancellationToken cancellationToken,
        UserRole role = UserRole.Member)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task SeedPermissionAsync(
        long userId,
        string permissionKey,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.UserPermissions.Add(new UserPermission
        {
            UserId = userId,
            PermissionKey = permissionKey,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return payload.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        (await ReadJsonAsync(response, cancellationToken)).GetProperty("error");

    private static byte[] Png(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IHDR"u8, PngHeader(width, height));
        WritePngChunk(stream, "IDAT"u8, [0x78, 0x9C]);
        WritePngChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static byte[] PngHeader(int width, int height)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        return header;
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        stream.Write(new byte[4]);
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        stream.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);

        Span<byte> dimensions = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(dimensions[..2], (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(dimensions[2..], (ushort)width);
        stream.Write(dimensions);

        stream.Write([0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00]);
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    private static byte[] WebpLossy(int width, int height)
    {
        var frame = new byte[10];
        frame[3] = 0x9D;
        frame[4] = 0x01;
        frame[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), (ushort)(width & 0x3FFF));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8, 2), (ushort)(height & 0x3FFF));
        return Webp(("VP8 "u8.ToArray(), frame));
    }

    private static byte[] Webp(params (byte[] Type, byte[] Data)[] chunks)
    {
        using var body = new MemoryStream();
        Span<byte> size = stackalloc byte[4];
        foreach (var (type, data) in chunks)
        {
            body.Write(type);
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
            body.Write(size);
            body.Write(data);
            if ((data.Length & 1) == 1)
            {
                body.WriteByte(0);
            }
        }

        var bodyBytes = body.ToArray();
        using var stream = new MemoryStream();
        stream.Write("RIFF"u8);
        Span<byte> riffSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)(4 + bodyBytes.Length));
        stream.Write(riffSize);
        stream.Write("WEBP"u8);
        stream.Write(bodyBytes);
        return stream.ToArray();
    }

    private sealed class SignedInClient(HttpClient client, string csrfToken) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public string CsrfToken { get; } = csrfToken;

        public void Dispose() => Client.Dispose();
    }
}
