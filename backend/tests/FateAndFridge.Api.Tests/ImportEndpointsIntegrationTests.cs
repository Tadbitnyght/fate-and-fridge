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
/// HTTP-level verification of the draft-only import routes
/// draft shape, multi-candidate
/// pages, structured fetch failures, throttling, the full permission matrix
/// and the no-write guarantee. Uses an injected fake <see cref="IImportFetcher"/>
/// with offline fixtures; no test touches the public internet.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class ImportEndpointsIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string ImportPath = "/api/v1/import/recipe-url";
    private const string CheckPath = "/api/v1/import/check";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "import-endpoints-test";
    private const string BaseUrl = "https://example.com/recipes/lasagna";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string JsonLdHtml =
        """
        <!DOCTYPE html>
        <html><head><title>Best Lasagna</title>
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"Recipe","name":"Lasagna","author":{"@type":"Person","name":"Jane Cook"},"image":"/lasagna.jpg","prepTime":"PT20M","cookTime":"PT45M","recipeIngredient":["1 1/2 cups all-purpose flour, sifted"],"recipeInstructions":[{"@type":"HowToStep","text":"Boil the noodles."}]}
        </script>
        </head><body></body></html>
        """;

    private const string MultiCandidateHtml =
        """
        <html><head>
        <script type="application/ld+json">
        {"@context":"https://schema.org","@graph":[{"@type":"Recipe","name":"One","recipeIngredient":["a"]},{"@type":"Recipe","name":"Two","recipeIngredient":["b"]}]}
        </script>
        </head><body></body></html>
        """;

    private const string HtmlFallbackHtml =
        """
        <html><head><title>Simple Pancakes</title></head><body>
        <h1>Simple Pancakes</h1>
        <h2>Ingredients</h2><ul><li>2 cups flour</li><li>1 cup milk</li></ul>
        <h2>Instructions</h2><ol><li>Mix everything.</li><li>Cook.</li></ol>
        </body></html>
        """;

    private const string MatchedIngredientsHtml =
        """
        <html><head><title>Matched</title>
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"Recipe","name":"Matched","recipeIngredient":["1 cup all-purpose flour","2 Green Onion","3 oz Unobtainium"],"recipeInstructions":[{"@type":"HowToStep","text":"Cook."}]}
        </script>
        </head><body></body></html>
        """;

    private const string NoRecipeHtml =
        "<html><head><title>Just an article</title></head><body><p>Nothing to see here.</p></body></html>";

    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Import_recipe_url_returns_the_draft_for_json_ld()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        await SeedUnitsAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("source").GetProperty("parse_method").GetString().Should().Be("json_ld");
        root.GetProperty("source").GetProperty("site").GetString().Should().Be("example.com");
        root.GetProperty("source").GetProperty("author").GetString().Should().Be("Jane Cook");
        root.GetProperty("source").GetProperty("page_title").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("capabilities").GetProperty("can_create_reference_data").GetBoolean().Should().BeTrue();

        var recipe = root.GetProperty("recipes")[0];
        recipe.GetProperty("index").GetInt32().Should().Be(0);
        recipe.GetProperty("name").GetString().Should().Be("Lasagna");
        recipe.GetProperty("prep_minutes").GetInt32().Should().Be(20);
        recipe.GetProperty("cook_minutes").GetInt32().Should().Be(45);
        recipe.GetProperty("source_url").GetString().Should().Be(BaseUrl);
        recipe.GetProperty("image_source_url").GetString().Should().Be("https://example.com/lasagna.jpg");
        recipe.GetProperty("warnings").EnumerateArray().Select(value => value.GetString())
            .Should().Contain(ImportWarningCodes.IngredientUnknown);

        var ingredient = recipe.GetProperty("ingredients")[0];
        ingredient.GetProperty("raw").GetString().Should().Be("1 1/2 cups all-purpose flour, sifted");
        ingredient.GetProperty("quantity").GetDecimal().Should().Be(1.5m);
        ingredient.GetProperty("name").GetString().Should().Be("all-purpose flour");
        ingredient.GetProperty("preparation_note").GetString().Should().Be("sifted");
        ingredient.GetProperty("is_unknown").GetBoolean().Should().BeTrue();
        ingredient.GetProperty("suggestions").GetArrayLength().Should().Be(0);

        var unit = ingredient.GetProperty("unit");
        unit.GetProperty("raw").GetString().Should().Be("cups");
        unit.GetProperty("matched_symbol").GetString().Should().Be("cup");
        unit.GetProperty("is_unknown").GetBoolean().Should().BeFalse();

        recipe.GetProperty("instruction_sections")[0].GetProperty("steps")[0].GetString()
            .Should().Be("Boil the noodles.");
        fetcher.PageCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Import_recipe_url_populates_ingredient_match_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        await SeedUnitsAsync(cancellationToken);
        var (flourId, scallionId, retiredId) = await SeedIngredientsAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(MatchedIngredientsHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var recipe = (await ReadJsonAsync(response, cancellationToken)).GetProperty("recipes")[0];

        var canonical = recipe.GetProperty("ingredients")[0];
        canonical.GetProperty("matched_ingredient_id").GetInt64().Should().Be(flourId);
        canonical.GetProperty("match_kind").GetString().Should().Be(IngredientMatcher.ExactKind);
        canonical.GetProperty("is_unknown").GetBoolean().Should().BeFalse();
        canonical.GetProperty("suggestions")[0].GetProperty("id").GetInt64().Should().Be(flourId);

        var alias = recipe.GetProperty("ingredients")[1];
        alias.GetProperty("matched_ingredient_id").GetInt64().Should().Be(scallionId);
        alias.GetProperty("match_kind").GetString().Should().Be(IngredientMatcher.AliasKind);
        alias.GetProperty("is_unknown").GetBoolean().Should().BeFalse();

        var unknown = recipe.GetProperty("ingredients")[2];
        unknown.GetProperty("matched_ingredient_id").ValueKind.Should().Be(JsonValueKind.Null);
        unknown.GetProperty("match_kind").ValueKind.Should().Be(JsonValueKind.Null);
        unknown.GetProperty("is_unknown").GetBoolean().Should().BeTrue();
        unknown.GetProperty("suggestions").EnumerateArray()
            .Select(suggestion => suggestion.GetProperty("id").GetInt64())
            .Should().NotContain(retiredId);

        recipe.GetProperty("warnings").EnumerateArray().Select(value => value.GetString())
            .Should().Contain(ImportWarningCodes.IngredientUnknown);
    }

    [Fact]
    public async Task Import_recipe_url_omits_unknown_warning_when_all_ingredients_match()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        await SeedUnitsAsync(cancellationToken);
        await SeedIngredientsAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var recipe = (await ReadJsonAsync(response, cancellationToken)).GetProperty("recipes")[0];
        recipe.GetProperty("warnings").EnumerateArray().Select(value => value.GetString())
            .Should().NotContain(ImportWarningCodes.IngredientUnknown);
    }

    [Fact]
    public async Task Import_recipe_url_returns_the_html_fallback_draft()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(HtmlFallbackHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("source").GetProperty("parse_method").GetString().Should().Be("html");
        root.GetProperty("recipes")[0].GetProperty("name").GetString().Should().Be("Simple Pancakes");
        root.GetProperty("recipes")[0].GetProperty("ingredients").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Import_recipe_url_returns_all_multi_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(MultiCandidateHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var recipes = (await ReadJsonAsync(response, cancellationToken)).GetProperty("recipes");
        recipes.GetArrayLength().Should().Be(2);
        recipes[0].GetProperty("index").GetInt32().Should().Be(0);
        recipes[0].GetProperty("name").GetString().Should().Be("One");
        recipes[1].GetProperty("index").GetInt32().Should().Be(1);
        recipes[1].GetProperty("name").GetString().Should().Be("Two");
    }

    [Fact]
    public async Task Import_recipe_url_maps_no_recipe_to_422()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(NoRecipeHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportNoRecipe);
    }

    [Theory]
    [InlineData(ImportFetchFailureReason.InvalidUrl, HttpStatusCode.BadRequest, ErrorCodes.ImportUrlInvalid)]
    [InlineData(ImportFetchFailureReason.BlockedTarget, HttpStatusCode.UnprocessableEntity, ErrorCodes.ImportUrlBlocked)]
    [InlineData(ImportFetchFailureReason.HttpError, HttpStatusCode.BadGateway, ErrorCodes.ImportFetchFailed)]
    [InlineData(ImportFetchFailureReason.Timeout, HttpStatusCode.BadGateway, ErrorCodes.ImportFetchFailed)]
    [InlineData(ImportFetchFailureReason.TooLarge, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.ImportTooLarge)]
    [InlineData(ImportFetchFailureReason.UnsupportedContentType, HttpStatusCode.UnsupportedMediaType, ErrorCodes.ImportUnsupportedContent)]
    public async Task Import_recipe_url_maps_fetch_failures_to_codes(
        ImportFetchFailureReason reason,
        HttpStatusCode status,
        string code)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageFailure(reason);
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(status);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task Import_recipe_url_rejects_a_missing_url_with_validation_failed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{}""", session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response, cancellationToken);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty("url", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Import_recipe_url_throttles_per_user_and_recovers_after_the_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher, clock, options => options.PageFetchesPerHour = 2);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        for (var i = 0; i < 2; i++)
        {
            var allowed = await SendJsonAsync(
                session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
                session.CsrfToken, cancellationToken);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var callsBeforeBlocked = fetcher.PageCallCount;
        var limited = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        limited.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
        var retryAfterSeconds = (int)limited.Headers.RetryAfter.Delta!.Value.TotalSeconds;
        retryAfterSeconds.Should().BeInRange(1, 3600);
        limited.Headers.TryGetValues("Retry-After", out var retryAfterValues).Should().BeTrue();
        int.Parse(retryAfterValues!.Single(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeInRange(1, 3600);
        (await ReadErrorAsync(limited, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportRateLimited);
        fetcher.PageCallCount.Should().Be(
            callsBeforeBlocked,
            "the throttle must reject before any outbound fetch");

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        var recovered = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);
        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Import_recipe_url_throttles_the_single_user_actor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher, clock, options => options.PageFetchesPerHour = 1);
        using var client = factory.CreateClient();

        var allowed = await SendJsonAsync(
            client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            csrfToken: null, cancellationToken);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);

        var callsBeforeBlocked = fetcher.PageCallCount;
        var limited = await SendJsonAsync(
            client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            csrfToken: null, cancellationToken);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.TryGetValues("Retry-After", out var retryAfterValues).Should().BeTrue();
        int.Parse(retryAfterValues!.Single(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeInRange(1, 3600);
        (await ReadErrorAsync(limited, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ImportRateLimited);
        fetcher.PageCallCount.Should().Be(callsBeforeBlocked);
    }

    [Theory]
    [InlineData("admin", HttpStatusCode.OK, true)]
    [InlineData("recipes_edit", HttpStatusCode.OK, false)]
    [InlineData("recipes_view", HttpStatusCode.Forbidden, false)]
    [InlineData("none", HttpStatusCode.Forbidden, false)]
    public async Task Import_recipe_url_permission_matrix(
        string actor,
        HttpStatusCode expected,
        bool expectedCapability)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);

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
                client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
                csrf, cancellationToken);

            response.StatusCode.Should().Be(expected);
            if (expected == HttpStatusCode.OK)
            {
                var root = await ReadJsonAsync(response, cancellationToken);
                root.GetProperty("capabilities").GetProperty("can_create_reference_data").GetBoolean()
                    .Should().Be(expectedCapability);
            }
        }
    }

    [Fact]
    public async Task Import_recipe_url_denies_a_reference_data_editor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var actor = await SeedUserAsync("reference", MemberPassword, cancellationToken);
        await SeedPermissionAsync(actor.Id, PermissionKeys.ReferenceDataEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl)));
        using var session = await SignInAsync(factory, "reference", MemberPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Import_check_allows_a_recipes_editor_via_implied_view()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var actor = await SeedUserAsync("editor", MemberPassword, cancellationToken);
        await SeedPermissionAsync(actor.Id, PermissionKeys.RecipesEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        using var session = await SignInAsync(factory, "editor", MemberPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, CheckPath, """{"name":"Lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Import_check_denies_a_reference_data_editor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var actor = await SeedUserAsync("reference", MemberPassword, cancellationToken);
        await SeedPermissionAsync(actor.Id, PermissionKeys.ReferenceDataEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        using var session = await SignInAsync(factory, "reference", MemberPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, CheckPath, """{"name":"Lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Import_recipe_url_requires_authentication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl)));

        var response = await SendJsonAsync(
            factory.CreateClient(), HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            csrfToken: null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Import_recipe_url_works_in_single_user_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            csrfToken: null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("capabilities").GetProperty("can_create_reference_data").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Import_recipe_url_creates_no_recipe_or_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        await using var context = fixture.CreateContext();
        var before = await EntityCountSnapshot.CaptureAsync(context, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ImportPath, """{"url":"https://example.com/recipes/lasagna"}""",
            session.CsrfToken, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await EntityCountSnapshot.CaptureAsync(context, cancellationToken);
        EntityCountSnapshot.ShouldBeUnchanged(before, after);
    }

    [Fact]
    public async Task Import_check_returns_source_url_and_name_matches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var seeded = await SeedRecipeAsync(
            "Saved Lasagna",
            RecipeService.BuildNormalizedSourceUrl(new Uri(BaseUrl), BaseUrl),
            cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            CheckPath,
            """{"source_url":"https://example.com/recipes/lasagna","name":"Lasagna"}""",
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("source_url_match").GetProperty("id").GetInt64().Should().Be(seeded.Id);
        root.GetProperty("source_url_match").GetProperty("name").GetString().Should().Be("Saved Lasagna");
        root.GetProperty("name_matches")[0].GetProperty("id").GetInt64().Should().Be(seeded.Id);
    }

    private const string StoredSourceUrl = "https://example.com/recipes/lasagna?b=2&a=1";

    /// <summary>
    /// Exact normalized-source-URL matrix. The
    /// stored recipe is folded with <c>RecipeService.BuildNormalizedSourceUrl</c>
    /// and the incoming URL varies by trailing slash, query order, fragment
    /// default port, case, and a genuine non-match.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/recipes/lasagna?a=1&b=2", true)]
    [InlineData("https://example.com/recipes/lasagna/?a=1&b=2", true)]
    [InlineData("https://example.com/recipes/lasagna?b=2&a=1", true)]
    [InlineData("https://example.com/recipes/lasagna?b=2&a=1#directions", true)]
    [InlineData("https://example.com:443/recipes/lasagna?b=2&a=1", true)]
    [InlineData("HTTPS://EXAMPLE.COM/recipes/lasagna?b=2&a=1", true)]
    [InlineData("https://example.com/recipes/other?b=2&a=1", false)]
    public async Task Import_check_matches_the_normalized_source_url_variants(string incomingUrl, bool matches)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var seeded = await SeedRecipeAsync(
            "Saved Lasagna",
            RecipeService.BuildNormalizedSourceUrl(new Uri(StoredSourceUrl), StoredSourceUrl),
            cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { source_url = incomingUrl }),
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var match = (await ReadJsonAsync(response, cancellationToken)).GetProperty("source_url_match");
        if (matches)
        {
            match.ValueKind.Should().Be(JsonValueKind.Object);
            match.GetProperty("id").GetInt64().Should().Be(seeded.Id);
        }
        else
        {
            match.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    /// <summary>
    /// The draft route populates <c>source_url_match</c> from the final
    /// post-redirect URL and must tolerate the same canonicalization variants.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/recipes/lasagna/?b=2&a=1", true)]
    [InlineData("https://example.com/recipes/lasagna?b=2&a=1#step", true)]
    [InlineData("https://example.com:443/recipes/lasagna?b=2&a=1", true)]
    [InlineData("https://example.com/recipes/other", false)]
    public async Task Import_recipe_url_populates_the_normalized_source_url_match(string finalUrl, bool matches)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var seeded = await SeedRecipeAsync(
            "Saved Lasagna",
            RecipeService.BuildNormalizedSourceUrl(new Uri(StoredSourceUrl), StoredSourceUrl),
            cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(finalUrl));
        using var factory = CreateFactory(fetcher);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            ImportPath,
            JsonSerializer.Serialize(new { url = finalUrl }),
            session.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var match = (await ReadJsonAsync(response, cancellationToken)).GetProperty("source_url_match");
        if (matches)
        {
            match.ValueKind.Should().Be(JsonValueKind.Object);
            match.GetProperty("id").GetInt64().Should().Be(seeded.Id);
        }
        else
        {
            match.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    /// <summary>
    /// The duplicate lookup must not write: a successful match leaves the recipe
    /// audit, and image row counts unchanged.
    /// </summary>
    [Fact]
    public async Task Import_check_source_url_match_creates_no_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        await SeedRecipeAsync(
            "Saved Lasagna",
            RecipeService.BuildNormalizedSourceUrl(new Uri(StoredSourceUrl), StoredSourceUrl),
            cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        await using var context = fixture.CreateContext();
        var before = await EntityCountSnapshot.CaptureAsync(context, cancellationToken);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            CheckPath,
            """{"source_url":"https://example.com/recipes/lasagna/","name":"Lasagna"}""",
            session.CsrfToken,
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await EntityCountSnapshot.CaptureAsync(context, cancellationToken);
        EntityCountSnapshot.ShouldBeUnchanged(before, after);
    }


    [Fact]
    public async Task Import_check_is_available_to_view_only_users()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var viewer = await SeedUserAsync("viewer", MemberPassword, cancellationToken);
        await SeedPermissionAsync(viewer.Id, PermissionKeys.RecipesView, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());
        using var session = await SignInAsync(factory, "viewer", MemberPassword, cancellationToken);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, CheckPath, """{"name":"Lasagna"}""",
            session.CsrfToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Import_check_rejects_unauthenticated_requests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = CreateFactory(new FakeImportFetcher());

        var response = await SendJsonAsync(
            factory.CreateClient(), HttpMethod.Post, CheckPath, """{"name":"Lasagna"}""",
            csrfToken: null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);
    }

    private ApiFactory CreateFactory(
        FakeImportFetcher fetcher,
        TestTimeProvider? clock = null,
        Action<ImportThrottleOptions>? configureThrottle = null) =>
        fixture.CreateFactory(configureServices: services =>
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
            cookies,
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
        string? normalizedSourceUrl,
        CancellationToken cancellationToken)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = new Recipe
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            NormalizedSourceUrl = normalizedSourceUrl,
            Status = RecipeStatus.Normal,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(cancellationToken);
        return recipe;
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Import Endpoints Household",
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

    /// <summary>
    /// Ensures the canonical units exist for unit resolution. The shared
    /// container may have had its units cleared by a sibling integration test
    /// while the seeded flag remained, so the flag is cleared and the
    /// production seeder re-run deterministically. Ingredient rows are cleared
    /// too so every test starts from a known, order-independent catalog; tests
    /// that need ingredients seed them afterwards.
    /// </summary>
    private async Task SeedUnitsAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM unit_aliases;
            DELETE FROM units;
            DELETE FROM application_settings WHERE key = 'reference_data_seeded';
            DELETE FROM recipe_ingredients WHERE ingredient_id IS NOT NULL;
            DELETE FROM ingredient_aliases;
            DELETE FROM ingredients;
            """,
            cancellationToken);
        await new ReferenceDataSeeder(context).SeedAsync(cancellationToken);
    }

    /// <summary>
    /// Seeds the ingredient-match fixture directly (the production seeder only
    /// creates units). Clears any leftover ingredient rows first because the M1
    /// reset does not touch the M2 ingredient tables. Returns the active flour
    /// the active scallion (aliased "Green Onion"), and the retired ingredient id.
    /// </summary>
    private async Task<(long FlourId, long ScallionId, long RetiredId)> SeedIngredientsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM recipe_ingredients WHERE ingredient_id IS NOT NULL;
            DELETE FROM ingredient_aliases;
            DELETE FROM ingredients;
            """,
            cancellationToken);

        var flour = AddIngredient(context, "All-Purpose Flour", active: true);
        var scallion = AddIngredient(context, "Scallion", active: true);
        var retired = AddIngredient(context, "Obsolete Herb", active: false);
        await context.SaveChangesAsync(cancellationToken);

        AddAlias(context, flour.Id, "plain flour");
        AddAlias(context, scallion.Id, "Green Onion");
        await context.SaveChangesAsync(cancellationToken);

        return (flour.Id, scallion.Id, retired.Id);

        static Ingredient AddIngredient(AppDbContext context, string canonicalName, bool active)
        {
            var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(canonicalName)!;
            var ingredient = new Ingredient
            {
                CanonicalName = normalized.Display,
                NormalizedName = normalized.Normalized,
                IsActive = active,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
            };
            context.Ingredients.Add(ingredient);
            return ingredient;
        }

        static void AddAlias(AppDbContext context, long ingredientId, string alias)
        {
            var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(alias)!;
            context.IngredientAliases.Add(new IngredientAlias
            {
                IngredientId = ingredientId,
                Alias = normalized.Display,
                NormalizedAlias = normalized.Normalized,
            });
        }
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

    private sealed class SignedInClient(HttpClient client, CookieContainer cookies, string csrfToken)
        : IDisposable
    {
        public HttpClient Client { get; } = client;

        public CookieContainer Cookies { get; } = cookies;

        public string CsrfToken { get; } = csrfToken;

        public void Dispose() => Client.Dispose();
    }
}
