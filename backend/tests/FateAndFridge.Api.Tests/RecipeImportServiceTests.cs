using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Imports;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Service-level verification of the draft-only import pipeline
/// (docs/M4_PLAN.md §2.1–§2.2) with an injected fake fetcher and offline
/// fixtures. A real PostgreSQL container backs the diagnostics reads; no test
/// touches the public internet.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class RecipeImportServiceTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string BaseUrl = "https://example.com/recipes/lasagna";

    private const string JsonLdHtml =
        """
        <!DOCTYPE html>
        <html><head><title>Best Lasagna</title>
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"Recipe","name":"Lasagna","author":{"@type":"Person","name":"Jane Cook"},"image":"/lasagna.jpg","prepTime":"PT20M","cookTime":"PT45M","recipeIngredient":["1 1/2 cups all-purpose flour, sifted"],"recipeInstructions":[{"@type":"HowToStep","text":"Boil the noodles."}]}
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

    private const string NoRecipeHtml =
        "<html><head><title>Just an article</title></head><body><p>Nothing to see here.</p></body></html>";

    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Import_json_ld_builds_a_complete_draft()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var result = await service.ImportAsync(BaseUrl, canCreateReferenceData: true, cancellationToken);

        var success = result.Should().BeOfType<ImportRecipeUrlResult.Success>().Subject;
        var draft = success.Draft;
        draft.Source.Url.Should().Be(BaseUrl);
        draft.Source.Site.Should().Be("example.com");
        draft.Source.ParseMethod.Should().Be("json_ld");
        draft.Source.Author.Should().Be("Jane Cook");
        draft.Source.PageTitle.Should().BeNull("page title enrichment is deferred past M4-04");
        draft.Source.FetchedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
        draft.Capabilities.CanCreateReferenceData.Should().BeTrue();
        draft.SourceUrlMatch.Should().BeNull();

        var recipe = draft.Recipes.Should().ContainSingle().Subject;
        recipe.Index.Should().Be(0);
        recipe.Name.Should().Be("Lasagna");
        recipe.Author.Should().Be("Jane Cook");
        recipe.SourceUrl.Should().Be(BaseUrl);
        recipe.SourceSite.Should().Be("example.com");
        recipe.PrepMinutes.Should().Be(20);
        recipe.CookMinutes.Should().Be(45);
        recipe.ImageSourceUrl.Should().Be("https://example.com/lasagna.jpg");
        recipe.Warnings.Should().Contain(ImportWarningCodes.IngredientUnknown);

        var ingredient = recipe.Ingredients.Should().ContainSingle().Subject;
        ingredient.Raw.Should().Be("1 1/2 cups all-purpose flour, sifted");
        ingredient.Quantity.Should().BeNull();
        ingredient.Name.Should().BeNull();
        ingredient.PreparationNote.Should().BeNull();
        ingredient.MatchedIngredientId.Should().BeNull();
        ingredient.MatchKind.Should().BeNull();
        ingredient.IsUnknown.Should().BeTrue();
        ingredient.Suggestions.Should().BeEmpty();
        ingredient.Unit.Raw.Should().BeNull();
        ingredient.Unit.IsUnknown.Should().BeTrue();
        ingredient.Unit.Suggestions.Should().BeEmpty();

        recipe.InstructionSections.Should().ContainSingle();
        recipe.InstructionSections[0].Title.Should().BeNull();
        recipe.InstructionSections[0].Steps.Should().Equal("Boil the noodles.");

        fetcher.PageCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Import_html_fallback_builds_a_draft()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fetcher = new FakeImportFetcher().OnPageHtml(HtmlFallbackHtml, new Uri(BaseUrl));
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var result = await service.ImportAsync(BaseUrl, canCreateReferenceData: false, cancellationToken);

        var success = result.Should().BeOfType<ImportRecipeUrlResult.Success>().Subject;
        success.Draft.Source.ParseMethod.Should().Be("html");
        success.Draft.Capabilities.CanCreateReferenceData.Should().BeFalse();
        var recipe = success.Draft.Recipes.Should().ContainSingle().Subject;
        recipe.Name.Should().Be("Simple Pancakes");
        recipe.Ingredients.Select(ingredient => ingredient.Raw)
            .Should().Equal("2 cups flour", "1 cup milk");
    }

    [Fact]
    public async Task Import_resolves_relative_image_and_source_against_the_final_url()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri("https://example.com/a/b/page"));
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var result = await service.ImportAsync(
            "https://example.com/redirected",
            canCreateReferenceData: false,
            cancellationToken);

        var success = result.Should().BeOfType<ImportRecipeUrlResult.Success>().Subject;
        success.Draft.Source.Url.Should().Be("https://example.com/a/b/page");
        var recipe = success.Draft.Recipes.Should().ContainSingle().Subject;
        recipe.SourceUrl.Should().Be("https://example.com/a/b/page");
        recipe.ImageSourceUrl.Should().Be("https://example.com/lasagna.jpg");
    }

    [Fact]
    public async Task Import_returns_the_exact_normalized_source_url_match()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await SeedRecipeAsync(
            "Saved Lasagna",
            normalizedSourceUrl: RecipeService.BuildNormalizedSourceUrl(new Uri(BaseUrl), BaseUrl),
            cancellationToken);
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var result = await service.ImportAsync(BaseUrl, canCreateReferenceData: false, cancellationToken);

        var success = result.Should().BeOfType<ImportRecipeUrlResult.Success>().Subject;
        success.Draft.SourceUrlMatch.Should().NotBeNull();
        success.Draft.SourceUrlMatch!.Id.Should().Be(seeded.Id);
        success.Draft.SourceUrlMatch.Name.Should().Be("Saved Lasagna");
    }

    [Fact]
    public async Task Import_name_matches_are_bounded_to_five()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var i = 1; i <= 7; i++)
        {
            await SeedRecipeAsync($"Lasagna Variation {i}", normalizedSourceUrl: null, cancellationToken);
        }

        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var result = await service.ImportAsync(BaseUrl, canCreateReferenceData: false, cancellationToken);

        var success = result.Should().BeOfType<ImportRecipeUrlResult.Success>().Subject;
        success.Draft.Recipes.Should().ContainSingle()
            .Which.NameMatches.Should().HaveCount(RecipeImportService.MaxNameMatches);
    }

    [Fact]
    public async Task Import_returns_no_recipe_when_nothing_parses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fetcher = new FakeImportFetcher().OnPageHtml(NoRecipeHtml, new Uri(BaseUrl));
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var result = await service.ImportAsync(BaseUrl, canCreateReferenceData: false, cancellationToken);

        result.Should().BeOfType<ImportRecipeUrlResult.NoRecipe>();
    }

    [Fact]
    public async Task Import_skips_the_html_fallback_on_absurdly_deep_markup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var deepHtml = string.Concat(
            string.Concat(Enumerable.Repeat("<div>", HtmlDepthGuard.MaxSafeTagDepth + 10)),
            "<h1>Deep Pancakes</h1><h2>Ingredients</h2><ul><li>1 cup flour</li></ul>");
        var fetcher = new FakeImportFetcher().OnPageHtml(deepHtml, new Uri(BaseUrl));
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var result = await service.ImportAsync(BaseUrl, canCreateReferenceData: false, cancellationToken);

        result.Should().BeOfType<ImportRecipeUrlResult.NoRecipe>(
            "the heuristic fallback is skipped when nesting exceeds the safe depth");
    }

    [Fact]
    public async Task Import_failure_is_reported_structurally()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fetcher = new FakeImportFetcher().OnPageFailure(ImportFetchFailureReason.BlockedTarget, "blocked");
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var result = await service.ImportAsync("http://localhost/recipe", canCreateReferenceData: false, cancellationToken);

        var failure = result.Should().BeOfType<ImportRecipeUrlResult.FetchFailed>().Subject;
        failure.Reason.Should().Be(ImportFetchFailureReason.BlockedTarget);
    }

    [Fact]
    public async Task Import_writes_no_entity_rows_on_success_for_any_family()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fetcher = new FakeImportFetcher().OnPageHtml(JsonLdHtml, new Uri(BaseUrl));
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var before = await EntityCountSnapshot.CaptureAsync(context, cancellationToken);

        var result = await service.ImportAsync(BaseUrl, canCreateReferenceData: true, cancellationToken);

        result.Should().BeOfType<ImportRecipeUrlResult.Success>();
        var after = await EntityCountSnapshot.CaptureAsync(context, cancellationToken);
        EntityCountSnapshot.ShouldBeUnchanged(before, after);
    }

    [Fact]
    public async Task Import_writes_no_entity_rows_on_failure_for_any_family()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fetcher = new FakeImportFetcher().OnPageFailure(ImportFetchFailureReason.HttpError, "boom");
        await using var context = fixture.CreateContext();
        var service = CreateService(context, fetcher);

        var before = await EntityCountSnapshot.CaptureAsync(context, cancellationToken);

        var result = await service.ImportAsync(BaseUrl, canCreateReferenceData: true, cancellationToken);

        result.Should().BeOfType<ImportRecipeUrlResult.FetchFailed>();
        var after = await EntityCountSnapshot.CaptureAsync(context, cancellationToken);
        EntityCountSnapshot.ShouldBeUnchanged(before, after);
    }

    [Fact]
    public async Task Check_returns_source_url_match_and_bounded_name_matches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await SeedRecipeAsync(
            "Saved Lasagna",
            normalizedSourceUrl: RecipeService.BuildNormalizedSourceUrl(new Uri(BaseUrl), BaseUrl),
            cancellationToken);
        await using var context = fixture.CreateContext();
        var service = CreateService(context, new FakeImportFetcher());

        var result = await service.CheckAsync(BaseUrl, "Lasagna", cancellationToken);

        result.SourceUrlMatch.Should().NotBeNull();
        result.SourceUrlMatch!.Id.Should().Be(seeded.Id);
        result.NameMatches.Should().ContainSingle().Which.Id.Should().Be(seeded.Id);
    }

    private static RecipeImportService CreateService(AppDbContext context, IImportFetcher fetcher) =>
        new(context, fetcher, new TestTimeProvider(DateTimeOffset.UtcNow), NullLogger<RecipeImportService>.Instance);

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
}
