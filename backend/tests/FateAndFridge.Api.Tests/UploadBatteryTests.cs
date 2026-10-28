using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
/// Adversarial upload battery: polyglot images that stay
/// inert, signature-vs-declared-type spoofing on both image routes, dimension
/// bombs declared by a tiny file, corrupt/truncated containers, and a chunked
/// oversized body rejected by the multipart limit before it is buffered.
/// </summary>
/// <remarks>
/// Complements <see cref="RecipeImageFormatDetectorTests"/> (pure detection)
/// <see cref="RecipeImageEndpointsIntegrationTests"/> (format/animation/size
/// policy) and <see cref="ImportImageEndpointIntegrationTests"/> (the
/// image-from-URL mapping): the cases here are the polyglot and spoofing shapes
/// those suites do not cover. Each test gets its own data directory, so stored
/// file assertions are isolated.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class UploadBatteryTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string RecipesPath = "/api/v1/recipes";
    private const string SourceUrl = "https://cdn.example.test/polyglot.jpg";
    private const string AdminPassword = "root horse battery staple";
    private const string TestUserAgent = "m14-upload-battery-test";
    private const string HtmlPayload = "<!DOCTYPE html><html><body><script>alert('pwned')</script></body></html>";
    private const string PhpPayload = "<?php system($_GET['c']); ?>";
    private const string ScriptPayload = "<script>document.location='https://evil.example/'</script>";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "faf-upload-battery-" + Guid.NewGuid().ToString("N"));

    private string ImagesRoot => Path.Combine(_dataDir, "images", "recipes");

    private static string ImagePath(long recipeId) => $"{RecipesPath}/{recipeId}/image";

    private static string FromUrlPath(long recipeId) => $"{RecipesPath}/{recipeId}/image/from-url";

    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

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

    // 1. Polyglots: a valid image with an appended payload stays inert

    [Fact]
    public async Task Upload_stores_a_polyglot_image_and_never_serves_the_appended_payload_as_markup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var payloads = new (string Label, byte[] Image, string DeclaredType, string ExpectedMediaType)[]
        {
            ("jpeg+html", Polyglot(Jpeg(320, 240), HtmlPayload), "image/jpeg", "image/jpeg"),
            ("jpeg+php", Polyglot(Jpeg(320, 240), PhpPayload), "image/jpeg", "image/jpeg"),
            ("png+html", Polyglot(Png(64, 64), HtmlPayload), "image/png", "image/png"),
            ("png+script", Polyglot(Png(64, 64), ScriptPayload), "image/png", "image/png"),
        };

        foreach (var (label, image, declaredType, expectedMediaType) in payloads)
        {
            var recipe = await SeedRecipeAsync($"Polyglot {label}", cancellationToken);

            var upload = await UploadImageAsync(
                session,
                recipe.Id,
                image,
                declaredType,
                altText: null,
                cancellationToken);
            upload.StatusCode.Should().Be(HttpStatusCode.OK, "{0} is a structurally valid image", label);

            var stored = await ReadStoredImageAsync(recipe.Id, cancellationToken);
            stored.Should().NotBeNull(label);
            Path.GetFileName(stored!.StoragePath).Should()
                .MatchRegex("^[0-9a-f]{32}\\.(jpg|png)$", "stored names are random and server-generated");
            var storedBytes = await File.ReadAllBytesAsync(stored.StoragePath, cancellationToken);
            storedBytes.Should().Equal(image, "the appended payload is stored verbatim, not stripped");

            var served = await session.Client.GetAsync(ImagePath(recipe.Id), cancellationToken);
            served.StatusCode.Should().Be(HttpStatusCode.OK);
            served.Content.Headers.ContentType!.MediaType.Should().Be(
                expectedMediaType,
                "the signature, never the filename or the payload, decides the served type");
            served.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which
                .Should().Be("nosniff", "a browser must not sniff the appended payload into markup");
            served.Content.Headers.ContentType!.MediaType.Should().NotBe("text/html");
            (await served.Content.ReadAsByteArrayAsync(cancellationToken)).Should().Equal(image);
        }

        ReadImageFiles().Should().HaveCount(payloads.Length);
        (await ReadAuditRowsAsync(cancellationToken)).Should().HaveCount(payloads.Length)
            .And.OnlyContain(row => row.EventType == AuditEventTypes.ImageUpdated);
    }

    [Fact]
    public async Task Import_from_url_stores_a_polyglot_image_and_never_serves_markup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var image = Polyglot(Jpeg(320, 240), HtmlPayload);
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/jpeg", image));
        using var factory = CreateFactory(fetcher);
        var recipe = await SeedRecipeAsync("Polyglot import", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            FromUrlPath(recipe.Id),
            $$"""{"source_url":"{{SourceUrl}}"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).Should().NotContain("<script>");

        var served = await session.Client.GetAsync(ImagePath(recipe.Id), cancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        served.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        (await served.Content.ReadAsByteArrayAsync(cancellationToken)).Should().Equal(image);
        (await File.ReadAllBytesAsync(ReadImageFiles().Should().ContainSingle().Which, cancellationToken))
            .Should().Equal(image);
    }

    // 2. Signature over declared type: spoofing both ways

    [Fact]
    public async Task Upload_rejects_a_script_capable_declared_type_even_when_the_bytes_are_an_image()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Spoofed declaration", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        var payloads = new (string Label, byte[] Bytes, string DeclaredType)[]
        {
            ("jpeg declared as svg", Jpeg(64, 64), "image/svg+xml"),
            ("jpeg declared as html", Jpeg(64, 64), "text/html"),
            ("png declared as html", Png(64, 64), "text/html"),
            ("webp declared as svg", WebpLossy(64, 64), "image/svg+xml"),
        };

        foreach (var (label, bytes, declaredType) in payloads)
        {
            await AssertRejectedUploadAsync(
                session,
                recipe.Id,
                imageId,
                payload: bytes,
                declaredType,
                HttpStatusCode.BadRequest,
                ErrorCodes.ImageInvalid,
                label,
                cancellationToken);
        }
    }

    [Fact]
    public async Task Upload_rejects_svg_and_html_bytes_even_when_declared_as_an_image()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Spoofed bytes", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        var payloads = new (string Label, byte[] Bytes, string DeclaredType)[]
        {
            ("svg declared as png", SvgBytes(), "image/png"),
            ("svg declared as jpeg", SvgBytes(), "image/jpeg"),
            ("svg declared as webp", SvgBytes(), "image/webp"),
            ("html declared as png", Encoding.UTF8.GetBytes(HtmlPayload), "image/png"),
            ("html declared as jpeg", Encoding.UTF8.GetBytes(HtmlPayload), "image/jpeg"),
        };

        foreach (var (label, bytes, declaredType) in payloads)
        {
            await AssertRejectedUploadAsync(
                session,
                recipe.Id,
                imageId,
                payload: bytes,
                declaredType,
                HttpStatusCode.UnsupportedMediaType,
                ErrorCodes.ImageTypeNotAllowed,
                label,
                cancellationToken);
        }
    }

    [Fact]
    public async Task Import_from_url_rejects_signature_spoofed_svg_and_html()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        var payloads = new (string Label, byte[] Bytes)[]
        {
            ("svg declared as png", SvgBytes()),
            ("html declared as png", Encoding.UTF8.GetBytes(HtmlPayload)),
            ("gif declared as webp", Encoding.UTF8.GetBytes("GIF89a")),
        };

        foreach (var (label, bytes) in payloads)
        {
            var fetcher = new FakeImportFetcher().OnImage(_ =>
                ImportFetchResult.Success(new Uri(SourceUrl), 200, "image/png", bytes));
            using var factory = CreateFactory(fetcher);
            var recipe = await SeedRecipeAsync($"Spoof import {label}", cancellationToken);
            using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

            var response = await SendJsonAsync(
                session.Client,
                FromUrlPath(recipe.Id),
                $$"""{"source_url":"{{SourceUrl}}"}""",
                session.CsrfToken,
                cancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType, label);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.ImportUnsupportedContent, label);
            ReadImageFiles().Should().BeEmpty("{0} must not be stored", label);
        }
    }

    // 3. Image bombs and corrupt containers

    [Fact]
    public async Task Upload_rejects_dimension_bombs_declared_by_a_tiny_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Dimension bomb", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        var payloads = new (string Label, byte[] Bytes, string DeclaredType)[]
        {
            // ~70 bytes that claim 65535x65535 pixels each.
            ("png 65535x65535", Png(65535, 65535), "image/png"),
            ("png 100000x100000", Png(100000, 100000), "image/png"),
            ("jpeg 65000x65000", Jpeg(65000, 65000), "image/jpeg"),
            ("webp 16384x16384", WebpExtended(16384, 16384, animatedFlag: false, animChunk: false), "image/webp"),
        };

        foreach (var (label, bytes, declaredType) in payloads)
        {
            bytes.Length.Should().BeLessThan(4096, "{0} is a tiny bomb header", label);

            await AssertRejectedUploadAsync(
                session,
                recipe.Id,
                imageId,
                payload: bytes,
                declaredType,
                HttpStatusCode.BadRequest,
                ErrorCodes.ImageDimensionsExceeded,
                label,
                cancellationToken);
        }
    }

    [Fact]
    public async Task Upload_rejects_corrupt_and_truncated_jpeg_png_and_webp_containers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Corrupt containers", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        var corruptJpeg = Jpeg(64, 64)[..6];
        var truncatedWebp = WebpLossy(64, 64)[..20];
        var lyingWebp = WebpLossy(64, 64);
        BinaryPrimitives.WriteUInt32LittleEndian(lyingWebp.AsSpan(4), 0x00FF_FFFF);
        var badPngHeader = Png(64, 64);
        BinaryPrimitives.WriteUInt32BigEndian(badPngHeader.AsSpan(8), 17);

        var payloads = new (string Label, byte[] Bytes, string DeclaredType)[]
        {
            ("jpeg truncated inside SOI", corruptJpeg, "image/jpeg"),
            ("webp truncated inside the first chunk", truncatedWebp, "image/webp"),
            ("webp RIFF size claims more than is present", lyingWebp, "image/webp"),
            ("png IHDR chunk length is not 13", badPngHeader, "image/png"),
        };

        foreach (var (label, bytes, declaredType) in payloads)
        {
            await AssertRejectedUploadAsync(
                session,
                recipe.Id,
                imageId,
                payload: bytes,
                declaredType,
                HttpStatusCode.BadRequest,
                ErrorCodes.ImageInvalid,
                label,
                cancellationToken);
        }
    }

    // 4. Request-shape limits

    [Fact]
    public async Task Upload_rejects_an_oversized_chunked_body_through_the_multipart_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Chunked oversize", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        // No Content-Length: the endpoint's declared-length pre-check cannot
        // fire, so the FormOptions multipart limit must reject it.
        using var content = new ChunkedMultipartBody(
            fieldName: "file",
            fileName: "bomb.png",
            contentType: "image/png",
            fillerBytes: RecipeImageService.MaxMultipartBodyBytes);
        using var request = new HttpRequestMessage(HttpMethod.Put, ImagePath(recipe.Id))
        {
            Content = content,
        };
        request.Headers.Add(CsrfMiddleware.HeaderName, session.CsrfToken);

        var response = await session.Client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImageTooLarge);
        ReadImageFiles().Should().BeEmpty();
        (await ReadStoredImageAsync(recipe.Id, cancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Upload_rejects_a_non_multipart_body()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Wrong shape", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Put, ImagePath(recipe.Id))
        {
            Content = new StringContent("{\"file\":\"not-multipart\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CsrfMiddleware.HeaderName, session.CsrfToken);

        var response = await session.Client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.UnsupportedMediaType);
        ReadImageFiles().Should().BeEmpty();
    }

    // Helpers

    private ApiFactory CreateFactory(FakeImportFetcher? fetcher = null) =>
        fixture.CreateFactory(_dataDir, configureServices: services =>
        {
            if (fetcher is not null)
            {
                services.RemoveAll<IImportFetcher>();
                services.AddSingleton<IImportFetcher>(fetcher);
            }
        });

    private string[] ReadImageFiles() =>
        Directory.Exists(ImagesRoot) ? Directory.GetFiles(ImagesRoot) : [];

    private async Task AssertRejectedUploadAsync(
        SignedInClient session,
        long recipeId,
        long originalImageId,
        byte[] payload,
        string declaredType,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string because,
        CancellationToken cancellationToken)
    {
        var response = await UploadImageAsync(
            session,
            recipeId,
            payload,
            declaredType,
            altText: null,
            cancellationToken);

        response.StatusCode.Should().Be(expectedStatus, because);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(expectedCode, because);
        (await ReadStoredImageAsync(recipeId, cancellationToken))!.Id.Should()
            .Be(originalImageId, "a rejected upload must not replace the active image");
        ReadImageFiles().Should().ContainSingle("{0} must not be written to disk", because);
    }

    private async Task<long> UploadSeedImageAsync(
        SignedInClient session,
        long recipeId,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var response = await UploadImageAsync(
            session,
            recipeId,
            bytes,
            "image/png",
            altText: null,
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seed image must upload");
        return (await ReadJsonAsync(response, cancellationToken)).GetProperty("id").GetInt64();
    }

    private static async Task<HttpResponseMessage> UploadImageAsync(
        SignedInClient session,
        long recipeId,
        byte[] bytes,
        string? declaredContentType,
        string? altText,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        if (declaredContentType is not null)
        {
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(declaredContentType);
        }

        content.Add(file, "file", "upload.bin");
        if (altText is not null)
        {
            content.Add(new StringContent(altText, Encoding.UTF8), "alt_text");
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, ImagePath(recipeId))
        {
            Content = content,
        };
        request.Headers.Add(CsrfMiddleware.HeaderName, session.CsrfToken);

        return await session.Client.SendAsync(request, cancellationToken);
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
            .Where(row => !row.EventType.StartsWith("auth."))
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task SeedHouseholdAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Upload Battery Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = AuthMode.Multiuser,
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

    private static byte[] Polyglot(byte[] image, string payload) =>
        [.. image, .. Encoding.UTF8.GetBytes(payload)];

    private static byte[] SvgBytes() =>
        Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

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

    private static byte[] WebpExtended(int width, int height, bool animatedFlag, bool animChunk)
    {
        var vp8x = new byte[10];
        vp8x[0] = animatedFlag ? (byte)0x02 : (byte)0x00;
        var encodedWidth = width - 1;
        var encodedHeight = height - 1;
        vp8x[4] = (byte)encodedWidth;
        vp8x[5] = (byte)(encodedWidth >> 8);
        vp8x[6] = (byte)(encodedWidth >> 16);
        vp8x[7] = (byte)encodedHeight;
        vp8x[8] = (byte)(encodedHeight >> 8);
        vp8x[9] = (byte)(encodedHeight >> 16);

        var chunks = new List<(byte[] Type, byte[] Data)>
        {
            ("VP8X"u8.ToArray(), vp8x),
        };
        if (animChunk)
        {
            chunks.Add(("ANIM"u8.ToArray(), new byte[6]));
        }

        chunks.Add(("VP8L"u8.ToArray(), WebpLosslessData(width, height)));
        return Webp(chunks.ToArray());
    }

    private static byte[] WebpLosslessData(int width, int height)
    {
        var data = new byte[5];
        data[0] = 0x2F;
        var bits = ((width - 1) & 0x3FFF) | (((height - 1) & 0x3FFF) << 14);
        data[1] = (byte)bits;
        data[2] = (byte)(bits >> 8);
        data[3] = (byte)(bits >> 16);
        data[4] = (byte)(bits >> 24);
        return data;
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

    /// <summary>
    /// A multipart body with no <c>Content-Length</c> (chunked), so the endpoint's
    /// declared-length pre-check cannot fire and the multipart reader limit has to
    /// reject it.
    /// </summary>
    private sealed class ChunkedMultipartBody : HttpContent
    {
        private const string Boundary = "----upload-boundary";
        private const int ChunkBytes = 64 * 1024;

        private readonly byte[] _prefix;
        private readonly long _fillerBytes;
        private readonly byte[] _suffix;

        public ChunkedMultipartBody(string fieldName, string fileName, string contentType, long fillerBytes)
        {
            _prefix = Encoding.UTF8.GetBytes(
                $"--{Boundary}\r\n" +
                $"Content-Disposition: form-data; name=\"{fieldName}\"; filename=\"{fileName}\"\r\n" +
                $"Content-Type: {contentType}\r\n\r\n");
            _suffix = Encoding.UTF8.GetBytes($"\r\n--{Boundary}--\r\n");
            _fillerBytes = fillerBytes;
            Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={Boundary}");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_prefix);
            var chunk = new byte[ChunkBytes];
            long written = 0;
            while (written < _fillerBytes)
            {
                var count = (int)Math.Min(ChunkBytes, _fillerBytes - written);
                await stream.WriteAsync(chunk.AsMemory(0, count));
                written += count;
            }

            await stream.WriteAsync(_suffix);
        }
    }
}
