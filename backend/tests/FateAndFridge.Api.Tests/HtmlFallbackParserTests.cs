using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Best-effort HTML/microdata fallback parser matrix (docs/M4_PLAN.md §1.5, §4;
/// docs/PRODUCT_SPEC.md §28; docs/SECURITY.md §7–§8). All fixtures are inline;
/// no network or browser automation is involved.
/// </summary>
public sealed class HtmlFallbackParserTests
{
    private const string PageUrl = "https://example.com/recipe";

    [Fact]
    public void Parse_microdata_recipe_maps_every_supported_field()
    {
        var html = """
            <!doctype html><html><body>
            <article itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Grandma&#39;s Lasagna</h1>
              <p itemprop="description">A <b>rich</b> &amp; cheesy bake.</p>
              <div itemprop="author" itemscope itemtype="https://schema.org/Person">
                <span itemprop="name">Jane Cook</span>
              </div>
              <img itemprop="image" src="https://example.com/lasagna.jpg" alt="lasagna">
              <time itemprop="prepTime" datetime="PT20M">20 min</time>
              <time itemprop="cookTime" datetime="PT45M">45 min</time>
              <ul>
                <li itemprop="recipeIngredient">1 box lasagna noodles</li>
                <li itemprop="recipeIngredient">2 cups shredded cheese</li>
              </ul>
              <div itemprop="recipeInstructions">
                <ol>
                  <li itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToStep">
                    <span itemprop="text">Boil the noodles.</span>
                  </li>
                  <li itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToStep">
                    <span itemprop="text">Layer and bake.</span>
                  </li>
                </ol>
              </div>
            </article>
            </body></html>
            """;

        var recipes = HtmlFallbackParser.Parse(html, PageUrl);

        recipes.Should().ContainSingle();
        var recipe = recipes[0];
        recipe.Name.Should().Be("Grandma's Lasagna");
        recipe.Description.Should().Be("A rich & cheesy bake.");
        recipe.Author.Should().Be("Jane Cook");
        recipe.SourceUrl.Should().Be(PageUrl);
        recipe.ImageUrl.Should().Be("https://example.com/lasagna.jpg");
        recipe.PrepMinutes.Should().Be(20);
        recipe.CookMinutes.Should().Be(45);
        recipe.IngredientLines.Should().Equal("1 box lasagna noodles", "2 cups shredded cheese");
        recipe.InstructionSections.Should().ContainSingle();
        recipe.InstructionSections[0].Title.Should().BeNull();
        recipe.InstructionSections[0].Steps.Should().Equal("Boil the noodles.", "Layer and bake.");
        recipe.Notes.Should().BeNull();
    }

    [Fact]
    public void Parse_microdata_how_to_sections_preserve_titles_and_order()
    {
        var html = """
            <html><body>
            <article itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Layered</h1>
              <div itemprop="recipeInstructions">
                <div itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToSection">
                  <h3 itemprop="name">Sauce</h3>
                  <ol>
                    <li itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToStep">
                      <span itemprop="text">Chop onions.</span>
                    </li>
                    <li itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToStep">
                      <span itemprop="text">Simmer.</span>
                    </li>
                  </ol>
                </div>
                <div itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToSection">
                  <h3 itemprop="name">Assembly</h3>
                  <ol>
                    <li itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToStep">
                      <span itemprop="text">Layer.</span>
                    </li>
                  </ol>
                </div>
              </div>
            </article>
            </body></html>
            """;

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();

        recipe.InstructionSections.Should().HaveCount(2);
        recipe.InstructionSections[0].Title.Should().Be("Sauce");
        recipe.InstructionSections[0].Steps.Should().Equal("Chop onions.", "Simmer.");
        recipe.InstructionSections[1].Title.Should().Be("Assembly");
        recipe.InstructionSections[1].Steps.Should().Equal("Layer.");
    }

    [Fact]
    public void Parse_returns_multiple_microdata_recipes_in_document_order()
    {
        var html = """
            <html><body>
            <div itemscope itemtype="https://schema.org/Recipe"><h1 itemprop="name">Alpha</h1></div>
            <div itemscope itemtype="http://schema.org/Recipe"><h1 itemprop="name">Beta</h1></div>
            </body></html>
            """;

        HtmlFallbackParser.Parse(html, PageUrl)
            .Select(recipe => recipe.Name)
            .Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public void Parse_microdata_reads_content_and_datetime_attributes()
    {
        var html = """
            <html><body>
            <div itemscope itemtype="https://schema.org/Recipe">
              <meta itemprop="name" content="Meta Named">
              <meta itemprop="description" content="Meta described.">
              <meta itemprop="recipeIngredient" content="1 cup sugar">
              <meta itemprop="recipeIngredient" content="2 eggs">
              <time itemprop="prepTime" datetime="PT5M">5 minutes</time>
            </div>
            </body></html>
            """;

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();

        recipe.Name.Should().Be("Meta Named");
        recipe.Description.Should().Be("Meta described.");
        recipe.PrepMinutes.Should().Be(5);
        recipe.IngredientLines.Should().Equal("1 cup sugar", "2 eggs");
    }

    [Fact]
    public void Parse_microdata_list_instructions_without_how_to_steps()
    {
        var html = """
            <html><body>
            <article itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Simple</h1>
              <ol itemprop="recipeInstructions">
                <li>Boil the pasta.</li>
                <li>Serve immediately.</li>
              </ol>
            </article>
            </body></html>
            """;

        var section = HtmlFallbackParser.Parse(html, PageUrl)
            .Single().InstructionSections.Single();

        section.Title.Should().BeNull();
        section.Steps.Should().Equal("Boil the pasta.", "Serve immediately.");
    }

    [Fact]
    public void Parse_heuristics_only_page_maps_title_meta_and_lists()
    {
        var html = """
            <html><head>
              <meta property="og:title" content="Weeknight Pasta">
              <meta property="og:description" content="Fast &amp; easy.">
              <meta property="og:image" content="https://example.com/pasta.jpg">
            </head><body>
              <h1>Weeknight Pasta</h1>
              <h2>Ingredients</h2>
              <ul>
                <li>200 g spaghetti</li>
                <li>3 cloves garlic</li>
              </ul>
              <h2>Directions</h2>
              <ol>
                <li>Boil the pasta.</li>
                <li>Toss with garlic.</li>
              </ol>
            </body></html>
            """;

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();

        recipe.Name.Should().Be("Weeknight Pasta");
        recipe.Description.Should().Be("Fast & easy.");
        recipe.ImageUrl.Should().Be("https://example.com/pasta.jpg");
        recipe.IngredientLines.Should().Equal("200 g spaghetti", "3 cloves garlic");
        recipe.InstructionSections.Single().Steps.Should().Equal("Boil the pasta.", "Toss with garlic.");
    }

    [Fact]
    public void Parse_heuristics_uses_og_title_when_no_h1()
    {
        var html = """
            <html><head><meta property="og:title" content="No Heading"></head><body>
              <h2>Ingredients</h2>
              <ul><li>1 carrot</li></ul>
            </body></html>
            """;

        HtmlFallbackParser.Parse(html, PageUrl).Single().Name.Should().Be("No Heading");
    }

    [Fact]
    public void Parse_heuristics_uses_class_labelled_containers()
    {
        var html = """
            <html><body>
              <h1>Chili</h1>
              <div class="wprm-recipe-ingredients-container">
                <ul><li>1 can beans</li><li>2 tbsp chili powder</li></ul>
              </div>
              <div class="wprm-recipe-instructions-container">
                <ol><li>Brown the beef.</li><li>Simmer.</li></ol>
              </div>
            </body></html>
            """;

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();

        recipe.IngredientLines.Should().Equal("1 can beans", "2 tbsp chili powder");
        recipe.InstructionSections.Single().Steps.Should().Equal("Brown the beef.", "Simmer.");
    }

    [Fact]
    public void Parse_heuristics_collects_paragraph_instructions()
    {
        var html = """
            <html><body>
              <h1>Toast</h1>
              <h2>Ingredients</h2>
              <ul><li>1 slice bread</li></ul>
              <h2>Instructions</h2>
              <p>Toast the bread.</p>
              <p>Spread the butter.</p>
            </body></html>
            """;

        var section = HtmlFallbackParser.Parse(html, PageUrl).Single().InstructionSections.Single();

        section.Steps.Should().Equal("Toast the bread.", "Spread the butter.");
    }

    [Theory]
    [InlineData("""<meta property="og:image" content="https://example.com/og.jpg">""", "https://example.com/og.jpg")]
    [InlineData("""<meta name="image" content="https://example.com/name.jpg">""", "https://example.com/name.jpg")]
    public void Parse_heuristics_reads_image_meta_variants(string meta, string expected)
    {
        var html = $$"""
            <html><head>{{meta}}</head><body>
              <h1>With Image</h1>
              <h2>Ingredients</h2>
              <ul><li>1 thing</li></ul>
            </body></html>
            """;

        HtmlFallbackParser.Parse(html, PageUrl).Single().ImageUrl.Should().Be(expected);
    }

    [Fact]
    public void Parse_heuristics_prefers_og_description_over_meta_description()
    {
        var html = """
            <html><head>
              <meta property="og:description" content="OG description">
              <meta name="description" content="Plain description">
            </head><body>
              <h1>Described</h1>
              <h2>Ingredients</h2>
              <ul><li>1 thing</li></ul>
            </body></html>
            """;

        HtmlFallbackParser.Parse(html, PageUrl).Single().Description.Should().Be("OG description");
    }

    [Fact]
    public void Parse_decodes_entities_in_microdata_text()
    {
        var html = """
            <html><body>
            <article itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Mac &amp; Cheese &#8212; Caf&#233; Style</h1>
              <ul>
                <li itemprop="recipeIngredient">1 &amp; a half cups milk</li>
              </ul>
              <div itemprop="recipeInstructions"><p>Stir &amp; serve.</p></div>
            </article>
            </body></html>
            """;

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();

        recipe.Name.Should().Be("Mac & Cheese \u2014 Caf\u00e9 Style");
        recipe.IngredientLines.Should().Equal("1 & a half cups milk");
        recipe.InstructionSections.Single().Steps.Should().Equal("Stir & serve.");
    }

    [Fact]
    public void Parse_handles_malformed_and_unclosed_markup_without_throwing()
    {
        var html = """
            <html><body>
            <div itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Broken Soup</h1>
              <p itemprop="description">Unclosed &amp; messy
              <ul>
                <li itemprop="recipeIngredient">1 carrot
                <li itemprop="recipeIngredient">2 cups broth
              <div itemprop="recipeInstructions"><p>Simmer everything
            </div>
            </body>
            """;

        FluentActions.Invoking(() => HtmlFallbackParser.Parse(html, PageUrl))
            .Should().NotThrow();

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();
        recipe.Name.Should().Be("Broken Soup");
        recipe.IngredientLines.Should().Contain("1 carrot");
        recipe.InstructionSections.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_returns_empty_for_a_page_without_a_recipe()
    {
        var html = """
            <html><head>
              <title>About us</title>
              <meta name="description" content="A company.">
            </head><body>
              <h1>About us</h1>
              <p>We sell widgets.</p>
            </body></html>
            """;

        HtmlFallbackParser.Parse(html, PageUrl).Should().BeEmpty();
    }

    [Fact]
    public void Parse_excludes_script_and_style_content()
    {
        var html = """
            <html><head><style>.x{color:red}</style></head><body>
            <script type="text/javascript">
              var html = '<h2>Ingredients</h2><ul><li>Fake ingredient</li></ul>';
              document.write(html);
            </script>
            <h1>Tasty</h1>
            <h2>Ingredients</h2>
            <ul><li>Real ingredient</li></ul>
            <h2>Directions</h2>
            <ol><li>Real step</li></ol>
            </body></html>
            """;

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();

        recipe.IngredientLines.Should().Equal("Real ingredient");
        recipe.InstructionSections.Single().Steps.Should().Equal("Real step");
    }

    [Fact]
    public void Parse_prefers_microdata_over_heuristics()
    {
        var html = """
            <html><head><meta property="og:title" content="OG Title"></head><body>
            <h1>Heuristic Title</h1>
            <h2>Ingredients</h2>
            <ul><li>Heuristic ingredient</li></ul>
            <h2>Directions</h2>
            <ol><li>Heuristic step</li></ol>
            <article itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Microdata Title</h1>
              <ul><li itemprop="recipeIngredient">Microdata ingredient</li></ul>
            </article>
            </body></html>
            """;

        var recipes = HtmlFallbackParser.Parse(html, PageUrl);

        recipes.Should().ContainSingle();
        recipes[0].Name.Should().Be("Microdata Title");
        recipes[0].IngredientLines.Should().Equal("Microdata ingredient");
    }

    [Fact]
    public void Parse_microdata_excludes_script_and_style_text_from_fields()
    {
        var html = """
            <html><body>
            <article itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Clean Name<script>var x = 'Leaked name';</script></h1>
              <p itemprop="description">Clean description<style>.x{content:'Leaked description'}</style></p>
              <ul>
                <li itemprop="recipeIngredient">1 cup flour<script>var y = 'Leaked ingredient';</script></li>
              </ul>
              <div itemprop="recipeInstructions">
                <ol>
                  <li itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToStep">
                    <span itemprop="text">Mix well.<style>.y{content:'Leaked step'}</style></span>
                  </li>
                </ol>
              </div>
            </article>
            </body></html>
            """;

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();

        recipe.Name.Should().Be("Clean Name");
        recipe.Description.Should().Be("Clean description");
        recipe.IngredientLines.Should().Equal("1 cup flour");
        recipe.InstructionSections.Single().Steps.Should().Equal("Mix well.");
    }

    [Fact]
    public void Parse_microdata_keeps_plain_steps_beside_typed_steps_in_order()
    {
        var html = """
            <html><body>
            <article itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Mixed</h1>
              <div itemprop="recipeInstructions">
                <p>Preheat the oven.</p>
                <ol>
                  <li itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToStep">
                    <span itemprop="text">Mix the batter.</span>
                  </li>
                </ol>
                <p>Let it rest.</p>
              </div>
            </article>
            </body></html>
            """;

        var section = HtmlFallbackParser.Parse(html, PageUrl).Single().InstructionSections.Single();

        section.Steps.Should().Equal("Preheat the oven.", "Mix the batter.", "Let it rest.");
    }

    [Fact]
    public void Parse_microdata_does_not_leak_nested_itemscope_properties()
    {
        var html = """
            <html><body>
            <article itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Root Name</h1>
              <div itemprop="author" itemscope itemtype="https://schema.org/Person">
                <span itemprop="name">Author Name</span>
              </div>
              <div itemprop="image" itemscope itemtype="https://schema.org/ImageObject">
                <link itemprop="url" href="https://example.com/pic.jpg">
              </div>
              <div itemprop="recipeInstructions">
                <ol>
                  <li itemprop="itemListElement" itemscope itemtype="https://schema.org/HowToStep">
                    <span itemprop="name">Step Name</span>
                    <span itemprop="text">Do the thing.</span>
                  </li>
                </ol>
              </div>
            </article>
            </body></html>
            """;

        var recipe = HtmlFallbackParser.Parse(html, PageUrl).Single();

        recipe.Name.Should().Be("Root Name");
        recipe.Author.Should().Be("Author Name");
        recipe.ImageUrl.Should().Be("https://example.com/pic.jpg");
        recipe.InstructionSections.Single().Steps.Should().Equal("Do the thing.");
    }

    [Fact]
    public void Parse_microdata_uses_first_duplicate_property()
    {
        var html = """
            <html><body>
            <div itemscope itemtype="https://schema.org/Recipe">
              <span itemprop="name">First</span>
              <span itemprop="name">Second</span>
              <span itemprop="recipeIngredient">1 egg</span>
            </div>
            </body></html>
            """;

        HtmlFallbackParser.Parse(html, PageUrl).Single().Name.Should().Be("First");
    }

    [Theory]
    [InlineData("https://schema.org/Recipe")]
    [InlineData("http://schema.org/Recipe")]
    [InlineData("https://schema.org/Recipe/")]
    [InlineData("schema:Recipe")]
    [InlineData("Recipe")]
    public void Parse_recognizes_supported_recipe_itemtype_forms(string itemtype)
    {
        var html = $$"""
            <html><body>
            <div itemscope itemtype="{{itemtype}}">
              <span itemprop="name">Typed</span>
              <span itemprop="recipeIngredient">1 thing</span>
            </div>
            </body></html>
            """;

        HtmlFallbackParser.Parse(html, PageUrl).Single().Name.Should().Be("Typed");
    }

    [Fact]
    public void Parse_handles_deeply_nested_markup_without_throwing()
    {
        var open = string.Concat(Enumerable.Repeat("<div>", 400));
        var close = string.Concat(Enumerable.Repeat("</div>", 400));
        var html = $$"""
            <html><body>
            <div itemscope itemtype="https://schema.org/Recipe">
              <h1 itemprop="name">Deep</h1>
              {{open}}<span itemprop="recipeIngredient">1 deep thing</span>{{close}}
            </div>
            </body></html>
            """;

        FluentActions.Invoking(() => HtmlFallbackParser.Parse(html, PageUrl))
            .Should().NotThrow();
        HtmlFallbackParser.Parse(html, PageUrl).Single().IngredientLines
            .Should().Equal("1 deep thing");
    }

    [Fact]
    public void Parse_returns_empty_for_null_or_blank_html()
    {
        HtmlFallbackParser.Parse(null).Should().BeEmpty();
        HtmlFallbackParser.Parse(string.Empty).Should().BeEmpty();
    }
}
