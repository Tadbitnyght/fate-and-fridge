using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the M3-04 recipe image lifecycle
/// (docs/M3_PLAN.md §1.6–§1.7, docs/SECURITY.md §9): multipart upload and
/// replacement, byte serving with ETag/304, alt-text updates, deletion, the
/// content policy rejections, the complete route permission matrix, and audit
/// coverage for every image write. Each test uses its own temporary data
/// directory so stored image files are isolated. Requires a Docker daemon; run
/// with <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class RecipeImageEndpointsIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string RecipesPath = "/api/v1/recipes";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "recipe-image-endpoints-test";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "faf-images-" + Guid.NewGuid().ToString("N"));

    private string ImagesRoot => Path.Combine(_dataDir, "images", "recipes");

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        // M1SetupFixture.ResetAsync deliberately leaves the M3 recipe tables
        // alone (M3 owns them), so clear them here — children first — to keep
        // this suite independent of execution order within the collection.
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM recipe_tags;
            DELETE FROM recipe_proteins;
            DELETE FROM recipe_instruction_steps;
            DELETE FROM recipe_instruction_sections;
            DELETE FROM recipe_ingredients;
            DELETE FROM recipe_ingredient_groups;
            DELETE FROM recipe_images;
            DELETE FROM recipes;
            """,
            cancellationToken);
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
    public async Task Upload_stores_the_image_embeds_it_in_recipe_responses_and_writes_an_audit_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Image Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var bytes = Png(640, 480);
        var upload = await UploadImageAsync(
            session,
            recipe.Id,
            bytes,
            "image/png",
            "  Fresh basil  ",
            cancellationToken);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);
        var image = await ReadJsonAsync(upload, cancellationToken);
        var imageId = image.GetProperty("id").GetInt64();
        imageId.Should().BeGreaterThan(0);
        image.GetProperty("alt_text").GetString().Should().Be("Fresh basil");
        image.GetProperty("source_url").ValueKind.Should().Be(JsonValueKind.Null);
        image.GetProperty("created_at").GetDateTimeOffset().Should().BeCloseTo(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        image.GetProperty("url").GetString().Should().Be($"{RecipesPath}/{recipe.Id}/image");

        var detail = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}", cancellationToken);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailImage = (await ReadJsonAsync(detail, cancellationToken)).GetProperty("image");
        detailImage.GetProperty("id").GetInt64().Should().Be(imageId);
        detailImage.GetProperty("alt_text").GetString().Should().Be("Fresh basil");
        detailImage.GetProperty("url").GetString().Should().Be($"{RecipesPath}/{recipe.Id}/image");

        var list = await session.Client.GetAsync(RecipesPath, cancellationToken);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = (await ReadJsonAsync(list, cancellationToken)).GetProperty("items")[0];
        item.GetProperty("id").GetInt64().Should().Be(recipe.Id);
        item.GetProperty("image").GetProperty("id").GetInt64().Should().Be(imageId);

        var files = ReadImageFiles();
        files.Should().ContainSingle();
        Path.GetFileName(files[0]).Should().MatchRegex("^[0-9a-f]{32}\\.png$");
        (await File.ReadAllBytesAsync(files[0], cancellationToken)).Should().Equal(bytes);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        var row = rows.Should().ContainSingle().Which;
        row.EventType.Should().Be(AuditEventTypes.ImageUpdated);
        row.EntityType.Should().Be(AuditEntityTypes.Recipe);
        row.EntityId.Should().Be(recipe.Id.ToString(CultureInfo.InvariantCulture));
        row.BeforeJson.Should().BeNull();
        row.ActorUserId.Should().Be(admin.Id);
        using var after = JsonDocument.Parse(row.AfterJson!);
        after.RootElement.GetProperty("id").GetInt64().Should().Be(imageId);
        after.RootElement.GetProperty("recipe_id").GetInt64().Should().Be(recipe.Id);
        after.RootElement.GetProperty("alt_text").GetString().Should().Be("Fresh basil");
        using var metadata = JsonDocument.Parse(row.MetadataJson);
        metadata.RootElement.GetProperty("content_type").GetString().Should().Be("image/png");
        metadata.RootElement.GetProperty("byte_size").GetInt64().Should().Be(bytes.LongLength);
        metadata.RootElement.GetProperty("width").GetInt32().Should().Be(640);
        metadata.RootElement.GetProperty("height").GetInt32().Should().Be(480);
    }

    [Fact]
    public async Task Serve_returns_canonical_bytes_content_type_etag_and_supports_not_modified()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var formats = new (string Label, byte[] Bytes, string ContentType, string Extension)[]
        {
            ("png", Png(120, 90), "image/png", "png"),
            ("jpeg", Jpeg(120, 90), "image/jpeg", "jpg"),
            ("webp", WebpLossy(120, 90), "image/webp", "webp"),
        };

        foreach (var format in formats)
        {
            var recipe = await SeedRecipeAsync($"Serve {format.Label}", SeedTimestamp, cancellationToken);
            var upload = await UploadImageAsync(
                session,
                recipe.Id,
                format.Bytes,
                format.ContentType,
                altText: null,
                cancellationToken);
            upload.StatusCode.Should().Be(HttpStatusCode.OK);
            var imageId = (await ReadJsonAsync(upload, cancellationToken)).GetProperty("id").GetInt64();

            var response = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}/image", cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be(format.ContentType);
            (await response.Content.ReadAsByteArrayAsync(cancellationToken)).Should().Equal(format.Bytes);
            response.Headers.ETag.Should().NotBeNull();
            response.Headers.ETag!.Tag.Should().Be($"\"{imageId}\"");
            response.Headers.GetValues("Cache-Control").Should().ContainSingle()
                .Which.Split(',').Select(value => value.Trim())
                .Should().BeEquivalentTo(new[] { "private", "no-cache" });
            response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");

            var stored = await ReadStoredImageAsync(recipe.Id, cancellationToken);
            stored.Should().NotBeNull();
            Path.GetExtension(stored!.StoragePath).Should().Be($".{format.Extension}");

            using var conditional = new HttpRequestMessage(HttpMethod.Get, $"{RecipesPath}/{recipe.Id}/image");
            conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            var notModified = await session.Client.SendAsync(conditional, cancellationToken);
            notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
            (await notModified.Content.ReadAsByteArrayAsync(cancellationToken)).Should().BeEmpty();
            notModified.Headers.ETag.Should().NotBeNull();
        }

        var withoutImage = await SeedRecipeAsync("No Image Serve", SeedTimestamp, cancellationToken);
        var missing = await session.Client.GetAsync($"{RecipesPath}/{withoutImage.Id}/image", cancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(missing, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);

        var unknown = await session.Client.GetAsync($"{RecipesPath}/987654321/image", cancellationToken);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(unknown, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Replace_swaps_the_row_and_file_and_changes_the_image_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Replace Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var firstBytes = Png(100, 100);
        var firstUpload = await UploadImageAsync(
            session,
            recipe.Id,
            firstBytes,
            "image/png",
            "first",
            cancellationToken);
        firstUpload.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstId = (await ReadJsonAsync(firstUpload, cancellationToken)).GetProperty("id").GetInt64();
        var firstFile = ReadImageFiles().Should().ContainSingle().Which;

        var secondBytes = Jpeg(200, 150);
        var secondUpload = await UploadImageAsync(
            session,
            recipe.Id,
            secondBytes,
            "image/jpeg",
            "second",
            cancellationToken);
        secondUpload.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await ReadJsonAsync(secondUpload, cancellationToken);
        var secondId = second.GetProperty("id").GetInt64();
        secondId.Should().NotBe(firstId, "replacement issues a new id and therefore a new URL/ETag");
        second.GetProperty("alt_text").GetString().Should().Be("second");

        File.Exists(firstFile).Should().BeFalse("the replaced file is deleted after the commit");
        var files = ReadImageFiles();
        files.Should().ContainSingle();
        Path.GetFileName(files[0]).Should().MatchRegex("^[0-9a-f]{32}\\.jpg$");
        (await File.ReadAllBytesAsync(files[0], cancellationToken)).Should().Equal(secondBytes);

        var stored = await ReadStoredImageAsync(recipe.Id, cancellationToken);
        stored.Should().NotBeNull();
        stored!.Id.Should().Be(secondId);

        var served = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}/image", cancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await served.Content.ReadAsByteArrayAsync(cancellationToken)).Should().Equal(secondBytes);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().HaveCount(2);
        rows[^1].EventType.Should().Be(AuditEventTypes.ImageUpdated);
        using var before = JsonDocument.Parse(rows[^1].BeforeJson!);
        before.RootElement.GetProperty("id").GetInt64().Should().Be(firstId);
        using var after = JsonDocument.Parse(rows[^1].AfterJson!);
        after.RootElement.GetProperty("id").GetInt64().Should().Be(secondId);
    }

    [Fact]
    public async Task Patch_alt_text_updates_clears_and_validates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Alt Text Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, Png(64, 64), cancellationToken);

        var patched = await PatchImageAltTextAsync(
            session,
            recipe.Id,
            """{"alt_text":"  Updated caption  "}""",
            cancellationToken);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var patchedRoot = await ReadJsonAsync(patched, cancellationToken);
        patchedRoot.GetProperty("id").GetInt64().Should().Be(imageId);
        patchedRoot.GetProperty("alt_text").GetString().Should().Be("Updated caption");

        var cleared = await PatchImageAltTextAsync(
            session,
            recipe.Id,
            """{"alt_text":null}""",
            cancellationToken);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(cleared, cancellationToken)).GetProperty("alt_text").ValueKind
            .Should().Be(JsonValueKind.Null);

        var blank = await PatchImageAltTextAsync(
            session,
            recipe.Id,
            """{"alt_text":"   "}""",
            cancellationToken);
        blank.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(blank, cancellationToken)).GetProperty("alt_text").ValueKind
            .Should().Be(JsonValueKind.Null);

        var tooLong = await PatchImageAltTextAsync(
            session,
            recipe.Id,
            $$"""{"alt_text":"{{new string('a', RecipeImageService.MaxAltTextLength + 1)}}"}""",
            cancellationToken);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(tooLong, cancellationToken);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty("alt_text", out _)
            .Should().BeTrue();

        var noImage = await SeedRecipeAsync("No Image Patch", SeedTimestamp, cancellationToken);
        var missing = await PatchImageAltTextAsync(
            session,
            noImage.Id,
            """{"alt_text":"ghost"}""",
            cancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(missing, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);

        var unknown = await PatchImageAltTextAsync(
            session,
            987654321,
            """{"alt_text":"ghost"}""",
            cancellationToken);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var detail = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}", cancellationToken);
        (await ReadJsonAsync(detail, cancellationToken)).GetProperty("image").GetProperty("alt_text")
            .ValueKind.Should().Be(JsonValueKind.Null);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().HaveCount(4, "upload plus three accepted alt-text patches");
        rows.Should().OnlyContain(row => row.EventType == AuditEventTypes.ImageUpdated);
        rows[^1].BeforeJson.Should().NotBeNull();
        rows[^1].AfterJson.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_removes_the_row_and_file_and_is_not_found_afterwards()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Delete Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, Png(64, 64), cancellationToken);
        var file = ReadImageFiles().Should().ContainSingle().Which;

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{RecipesPath}/{recipe.Id}/image",
            json: null,
            session.CsrfToken,
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ReadStoredImageAsync(recipe.Id, cancellationToken)).Should().BeNull();
        File.Exists(file).Should().BeFalse();
        ReadImageFiles().Should().BeEmpty();

        var second = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{RecipesPath}/{recipe.Id}/image",
            json: null,
            session.CsrfToken,
            cancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(second, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);

        var served = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}/image", cancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var detail = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}", cancellationToken);
        (await ReadJsonAsync(detail, cancellationToken)).GetProperty("image").ValueKind
            .Should().Be(JsonValueKind.Null);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().HaveCount(2);
        var removed = rows[^1];
        removed.EventType.Should().Be(AuditEventTypes.ImageRemoved);
        removed.AfterJson.Should().BeNull();
        using var before = JsonDocument.Parse(removed.BeforeJson!);
        before.RootElement.GetProperty("id").GetInt64().Should().Be(imageId);
    }

    [Fact]
    public async Task Rejects_unsupported_formats_with_type_not_allowed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Unsupported Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        var payloads = new (string Label, byte[] Bytes, string DeclaredType)[]
        {
            ("svg", Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"), "image/svg+xml"),
            ("gif", Encoding.ASCII.GetBytes("GIF89a"), "image/gif"),
            ("html", Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>hello</body></html>"), "text/html"),
        };

        foreach (var (label, payload, declaredType) in payloads)
        {
            await AssertRejectedUploadAsync(
                session,
                recipe.Id,
                original,
                imageId,
                payload,
                declaredType,
                HttpStatusCode.UnsupportedMediaType,
                ErrorCodes.ImageTypeNotAllowed,
                $"{label} is not a supported image format",
                cancellationToken);
        }
    }

    [Fact]
    public async Task Rejects_truncated_and_mismatched_content_with_image_invalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Malformed Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            Png(64, 64)[..20],
            "image/png",
            HttpStatusCode.BadRequest,
            ErrorCodes.ImageInvalid,
            "the png payload is truncated",
            cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            Png(64, 64),
            "image/jpeg",
            HttpStatusCode.BadRequest,
            ErrorCodes.ImageInvalid,
            "png bytes declared as jpeg",
            cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            Jpeg(64, 64),
            "image/png",
            HttpStatusCode.BadRequest,
            ErrorCodes.ImageInvalid,
            "jpeg bytes declared as png",
            cancellationToken);
    }

    [Fact]
    public async Task Rejects_uploads_over_five_mib_or_the_multipart_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Oversized Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            new byte[RecipeImageService.MaxFileBytes + 1],
            "image/png",
            HttpStatusCode.RequestEntityTooLarge,
            ErrorCodes.ImageTooLarge,
            "the file exceeds 5 MiB",
            cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            new byte[RecipeImageService.MaxMultipartBodyBytes + 1],
            "image/png",
            HttpStatusCode.RequestEntityTooLarge,
            ErrorCodes.ImageTooLarge,
            "the multipart body exceeds the reader limit",
            cancellationToken);
    }

    [Fact]
    public async Task Rejects_images_beyond_the_dimension_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Dimension Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            Png(RecipeImageService.MaxDimension + 1, 10),
            "image/png",
            HttpStatusCode.BadRequest,
            ErrorCodes.ImageDimensionsExceeded,
            "the png width exceeds the limit",
            cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            Jpeg(10, RecipeImageService.MaxDimension + 1),
            "image/jpeg",
            HttpStatusCode.BadRequest,
            ErrorCodes.ImageDimensionsExceeded,
            "the jpeg height exceeds the limit",
            cancellationToken);
    }

    [Fact]
    public async Task Rejects_animated_png_and_webp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Animated Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var original = Png(64, 64);
        var imageId = await UploadSeedImageAsync(session, recipe.Id, original, cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            Png(64, 64, animated: true),
            "image/png",
            HttpStatusCode.BadRequest,
            ErrorCodes.ImageInvalid,
            "the png carries an animation control chunk",
            cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            WebpExtended(64, 64, animatedFlag: true, animChunk: false),
            "image/webp",
            HttpStatusCode.BadRequest,
            ErrorCodes.ImageInvalid,
            "the webp sets the animation flag",
            cancellationToken);

        await AssertRejectedUploadAsync(
            session,
            recipe.Id,
            original,
            imageId,
            WebpExtended(64, 64, animatedFlag: false, animChunk: true),
            "image/webp",
            HttpStatusCode.BadRequest,
            ErrorCodes.ImageInvalid,
            "the webp carries an anim chunk",
            cancellationToken);
    }

    [Fact]
    public async Task Admin_can_manage_recipe_images()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Admin Image", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        await AssertFullImageAccessAsync(session.Client, session.CsrfToken, recipe.Id, cancellationToken);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.ImageUpdated,
            AuditEventTypes.ImageUpdated,
            AuditEventTypes.ImageRemoved);
        rows.Should().OnlyContain(row => row.ActorUserId == admin.Id);
    }

    [Fact]
    public async Task Member_with_recipes_edit_can_manage_recipe_images()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var jane = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(jane.Id, PermissionKeys.RecipesEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Edit Image", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        // recipes.edit implies recipes.view, so the member can also read.
        await AssertFullImageAccessAsync(session.Client, session.CsrfToken, recipe.Id, cancellationToken);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(row => row.ActorUserId == jane.Id);
    }

    [Fact]
    public async Task Member_with_recipes_view_can_only_read_recipe_images()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var jane = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(jane.Id, PermissionKeys.RecipesView, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("View Image", SeedTimestamp, cancellationToken);
        using var adminSession = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        await UploadSeedImageAsync(adminSession, recipe.Id, Png(64, 64), cancellationToken);

        using var memberSession = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);
        var read = await memberSession.Client.GetAsync($"{RecipesPath}/{recipe.Id}/image", cancellationToken);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertImageWritesForbiddenAsync(memberSession, recipe.Id, cancellationToken);
        (await ReadStoredImageAsync(recipe.Id, cancellationToken)).Should().NotBeNull(
            "the denied writes leave the image untouched");
    }

    [Fact]
    public async Task Member_without_recipe_grants_is_forbidden_everywhere()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Denied Image", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        var read = await session.Client.GetAsync($"{RecipesPath}/{recipe.Id}/image", cancellationToken);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorAsync(read, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);

        await AssertImageWritesForbiddenAsync(session, recipe.Id, cancellationToken);
        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
        ReadImageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthenticated_image_requests_receive_auth_required()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("Anonymous Image", SeedTimestamp, cancellationToken);
        using var client = factory.CreateDefaultClient();

        var path = $"{RecipesPath}/{recipe.Id}/image";

        var read = await client.GetAsync(path, cancellationToken);
        read.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(read, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);

        var upload = await SendImageAsync(
            client,
            recipe.Id,
            Png(64, 64),
            "image/png",
            altText: null,
            csrfToken: null,
            cancellationToken);
        upload.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(upload, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);

        var patch = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            path,
            """{"alt_text":"anonymous"}""",
            csrfToken: null,
            cancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(patch, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);

        var delete = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            path,
            json: null,
            csrfToken: null,
            cancellationToken);
        delete.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(delete, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
        ReadImageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Single_user_system_actor_manages_recipe_images_without_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory();
        var recipe = await SeedRecipeAsync("System Image", SeedTimestamp, cancellationToken);
        using var client = factory.CreateClient();

        await AssertFullImageAccessAsync(client, csrfToken: null, recipe.Id, cancellationToken);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.ImageUpdated,
            AuditEventTypes.ImageUpdated,
            AuditEventTypes.ImageRemoved);
        rows.Should().OnlyContain(row => row.ActorUserId == null);
    }

    private ApiFactory CreateFactory() => fixture.CreateFactory(_dataDir);

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
        var upload = await UploadImageAsync(
            session,
            recipeId,
            bytes,
            "image/png",
            "original",
            cancellationToken);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ReadJsonAsync(upload, cancellationToken)).GetProperty("id").GetInt64();
    }

    private async Task AssertRejectedUploadAsync(
        SignedInClient session,
        long recipeId,
        byte[] originalBytes,
        long imageId,
        byte[] payload,
        string? declaredContentType,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string because,
        CancellationToken cancellationToken)
    {
        var filesBefore = ReadImageFiles();
        var auditBefore = (await ReadAuditRowsAsync(cancellationToken)).Count;

        var response = await UploadImageAsync(
            session,
            recipeId,
            payload,
            declaredContentType,
            altText: null,
            cancellationToken);
        response.StatusCode.Should().Be(expectedStatus, because);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(expectedCode, because);

        ReadImageFiles().Should().BeEquivalentTo(filesBefore, "a rejected upload leaves no orphan file");
        var stored = await ReadStoredImageAsync(recipeId, cancellationToken);
        stored.Should().NotBeNull("the previous image row survives a rejected upload");
        stored!.Id.Should().Be(imageId, "the previous image row survives a rejected upload");

        var served = await session.Client.GetAsync($"{RecipesPath}/{recipeId}/image", cancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync(cancellationToken)).Should()
            .Equal(originalBytes, "the previous image bytes are untouched");
        (await ReadAuditRowsAsync(cancellationToken)).Should()
            .HaveCount(auditBefore, "a rejected upload writes no audit row");
    }

    private static async Task AssertFullImageAccessAsync(
        HttpClient client,
        string? csrfToken,
        long recipeId,
        CancellationToken cancellationToken)
    {
        var upload = await SendImageAsync(
            client,
            recipeId,
            Png(80, 60),
            "image/png",
            "matrix",
            csrfToken,
            cancellationToken);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        var read = await client.GetAsync($"{RecipesPath}/{recipeId}/image", cancellationToken);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        var patch = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{RecipesPath}/{recipeId}/image",
            """{"alt_text":"renamed"}""",
            csrfToken,
            cancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var delete = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{RecipesPath}/{recipeId}/image",
            json: null,
            csrfToken,
            cancellationToken);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static async Task AssertImageWritesForbiddenAsync(
        SignedInClient session,
        long recipeId,
        CancellationToken cancellationToken)
    {
        var path = $"{RecipesPath}/{recipeId}/image";

        var upload = await SendImageAsync(
            session.Client,
            recipeId,
            Png(80, 60),
            "image/png",
            altText: null,
            session.CsrfToken,
            cancellationToken);
        upload.StatusCode.Should().Be(HttpStatusCode.Forbidden, "PUT {0} requires recipes.edit", path);
        (await ReadErrorAsync(upload, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);

        var patch = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            path,
            """{"alt_text":"denied"}""",
            session.CsrfToken,
            cancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.Forbidden, "PATCH {0} requires recipes.edit", path);
        (await ReadErrorAsync(patch, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);

        var delete = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            path,
            json: null,
            session.CsrfToken,
            cancellationToken);
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden, "DELETE {0} requires recipes.edit", path);
        (await ReadErrorAsync(delete, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);
    }

    private static Task<HttpResponseMessage> UploadImageAsync(
        SignedInClient session,
        long recipeId,
        byte[] bytes,
        string? declaredContentType,
        string? altText,
        CancellationToken cancellationToken) =>
        SendImageAsync(
            session.Client,
            recipeId,
            bytes,
            declaredContentType,
            altText,
            session.CsrfToken,
            cancellationToken);

    private static async Task<HttpResponseMessage> SendImageAsync(
        HttpClient client,
        long recipeId,
        byte[] bytes,
        string? declaredContentType,
        string? altText,
        string? csrfToken,
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

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{RecipesPath}/{recipeId}/image")
        {
            Content = content,
        };
        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static Task<HttpResponseMessage> PatchImageAltTextAsync(
        SignedInClient session,
        long recipeId,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{RecipesPath}/{recipeId}/image",
            json,
            session.CsrfToken,
            cancellationToken);

    private static async Task<SignedInClient> SignInAsync(
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

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
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

    private async Task<Recipe> SeedRecipeAsync(
        string name,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = new Recipe
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            Status = RecipeStatus.Normal,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
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
            HouseholdName = "Recipe Image Endpoints Household",
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

    private static byte[] Png(int width, int height, bool animated = false, bool includeIdat = true)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IHDR"u8, PngHeader(width, height));
        if (animated)
        {
            WritePngChunk(stream, "acTL"u8, new byte[8]);
        }

        if (includeIdat)
        {
            WritePngChunk(stream, "IDAT"u8, [0x78, 0x9C]);
        }

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
}
