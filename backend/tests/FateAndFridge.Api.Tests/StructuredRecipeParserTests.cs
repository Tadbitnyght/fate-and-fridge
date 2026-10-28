using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Schema.org JSON-LD recipe extraction matrix.
/// </summary>
public sealed class StructuredRecipeParserTests
{
    private const string PageUrl = "https://example.com/garlic-pasta";

    private static string Page(string json, string type = "application/ld+json") =>
        $"<!doctype html><html><head><script type=\"{type}\">{json}</script></head><body></body></html>";

    [Fact]
    public void Parse_simple_recipe_maps_every_supported_field()
    {
        var html = Page(
            """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Garlic Pasta",
              "description": "A <b>simple</b> weeknight pasta &amp; garlic dish.",
              "author": { "@type": "Person", "name": "Jane Cook" },
              "recipeIngredient": [
                "200 g spaghetti",
                { "name": "3 cloves garlic" }
              ],
              "recipeInstructions": [
                { "@type": "HowToStep", "text": "Boil the pasta." },
                { "@type": "HowToStep", "text": "Toss with garlic." }
              ],
              "image": "https://example.com/pasta.jpg",
              "prepTime": "PT10M",
              "cookTime": "PT20M"
            }
            """);

        var recipes = StructuredRecipeParser.Parse(html, PageUrl);

        recipes.Should().ContainSingle();
        var recipe = recipes[0];
        recipe.Name.Should().Be("Garlic Pasta");
        recipe.Description.Should().Be("A simple weeknight pasta & garlic dish.");
        recipe.Author.Should().Be("Jane Cook");
        recipe.SourceUrl.Should().Be(PageUrl);
        recipe.ImageUrl.Should().Be("https://example.com/pasta.jpg");
        recipe.PrepMinutes.Should().Be(10);
        recipe.CookMinutes.Should().Be(20);
        recipe.IngredientLines.Should().Equal("200 g spaghetti", "3 cloves garlic");
        recipe.InstructionSections.Should().ContainSingle();
        recipe.InstructionSections[0].Title.Should().BeNull();
        recipe.InstructionSections[0].Steps.Should().Equal("Boil the pasta.", "Toss with garlic.");
        recipe.Notes.Should().BeNull();
    }

    [Fact]
    public void Parse_reads_recipes_from_graph_in_document_order()
    {
        var html = Page(
            """
            {
              "@context": "https://schema.org",
              "@graph": [
                { "@type": "WebPage", "name": "My recipes" },
                { "@type": "Recipe", "name": "Lasagna" },
                { "@type": "Recipe", "name": "Risotto" }
              ]
            }
            """);

        var recipes = StructuredRecipeParser.Parse(html, PageUrl);

        recipes.Select(r => r.Name).Should().Equal("Lasagna", "Risotto");
    }

    [Fact]
    public void Parse_returns_all_recipes_from_a_top_level_array()
    {
        var html = Page(
            """
            [
              { "@type": "Recipe", "name": "Alpha" },
              { "@type": "Recipe", "name": "Beta" }
            ]
            """);

        StructuredRecipeParser.Parse(html, PageUrl).Select(r => r.Name).Should().Equal("Alpha", "Beta");
    }

    [Theory]
    [InlineData("\"Recipe\"")]
    [InlineData("\"recipe\"")]
    [InlineData("\"https://schema.org/Recipe\"")]
    [InlineData("\"http://schema.org/Recipe\"")]
    [InlineData("\"schema:Recipe\"")]
    [InlineData("[\"NewsArticle\", \"Recipe\"]")]
    public void Parse_matches_recipe_type_in_every_supported_form(string typeJson)
    {
        var html = Page($$"""{ "@type": {{typeJson}}, "name": "Match" }""");

        StructuredRecipeParser.Parse(html, PageUrl).Should().ContainSingle()
            .Which.Name.Should().Be("Match");
    }

    [Fact]
    public void Parse_ignores_non_recipe_types()
    {
        var html = Page("""{ "@type": "Article", "name": "Not a recipe" }""");

        StructuredRecipeParser.Parse(html, PageUrl).Should().BeEmpty();
    }

    [Fact]
    public void Parse_flattens_how_to_sections_and_preserves_nested_sections()
    {
        var html = Page(
            """
            {
              "@type": "Recipe",
              "name": "Layered",
              "recipeInstructions": [
                {
                  "@type": "HowToSection",
                  "name": "Sauce",
                  "itemListElement": [
                    { "@type": "HowToStep", "text": "Chop onions." },
                    { "@type": "HowToStep", "text": "Simmer." }
                  ]
                },
                {
                  "@type": "HowToSection",
                  "name": "Assembly",
                  "itemListElement": [
                    { "@type": "HowToStep", "text": "Layer." },
                    {
                      "@type": "HowToSection",
                      "name": "Bake",
                      "itemListElement": [
                        { "@type": "HowToStep", "text": "Bake 30 minutes." }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var recipe = StructuredRecipeParser.Parse(html, PageUrl).Single();

        recipe.InstructionSections.Should().HaveCount(3);
        recipe.InstructionSections[0].Title.Should().Be("Sauce");
        recipe.InstructionSections[0].Steps.Should().Equal("Chop onions.", "Simmer.");
        recipe.InstructionSections[1].Title.Should().Be("Assembly");
        recipe.InstructionSections[1].Steps.Should().Equal("Layer.");
        recipe.InstructionSections[2].Title.Should().Be("Bake");
        recipe.InstructionSections[2].Steps.Should().Equal("Bake 30 minutes.");
    }

    [Fact]
    public void Parse_accepts_a_plain_string_instruction()
    {
        var html = Page(
            """
            { "@type": "Recipe", "name": "Simple", "recipeInstructions": "Boil the pasta. Serve immediately." }
            """);

        var section = StructuredRecipeParser.Parse(html, PageUrl).Single().InstructionSections.Single();

        section.Title.Should().BeNull();
        section.Steps.Should().Equal("Boil the pasta. Serve immediately.");
    }

    [Fact]
    public void Parse_accepts_a_string_array_instruction()
    {
        var html = Page(
            """
            { "@type": "Recipe", "name": "Simple", "recipeInstructions": ["Step one.", "Step two."] }
            """);

        var section = StructuredRecipeParser.Parse(html, PageUrl).Single().InstructionSections.Single();

        section.Title.Should().BeNull();
        section.Steps.Should().Equal("Step one.", "Step two.");
    }

    [Fact]
    public void Parse_uses_how_to_step_name_when_text_is_absent()
    {
        var html = Page(
            """
            {
              "@type": "Recipe",
              "name": "Named step",
              "recipeInstructions": [ { "@type": "HowToStep", "name": "Stir well." } ]
            }
            """);

        StructuredRecipeParser.Parse(html, PageUrl).Single()
            .InstructionSections.Single().Steps.Should().Equal("Stir well.");
    }

    [Theory]
    [InlineData("\"https://example.com/a.jpg\"", "https://example.com/a.jpg")]
    [InlineData("""["https://example.com/first.jpg", "https://example.com/second.jpg"]""", "https://example.com/first.jpg")]
    [InlineData("""{ "@type": "ImageObject", "url": "https://example.com/obj.jpg" }""", "https://example.com/obj.jpg")]
    [InlineData("""[{ "@type": "ImageObject", "url": "https://example.com/in-array.jpg" }]""", "https://example.com/in-array.jpg")]
    public void Parse_extracts_first_image_url_variant(string imageJson, string expected)
    {
        var html = Page($$"""{ "@type": "Recipe", "name": "Img", "image": {{imageJson}} }""");

        StructuredRecipeParser.Parse(html, PageUrl).Single().ImageUrl.Should().Be(expected);
    }

    [Fact]
    public void Parse_handles_image_without_url_as_absent()
    {
        var html = Page("""{ "@type": "Recipe", "name": "Img", "image": { "@type": "ImageObject" } }""");

        StructuredRecipeParser.Parse(html, PageUrl).Single().ImageUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("\"Jane Cook\"", "Jane Cook")]
    [InlineData("{ \"@type\": \"Person\", \"name\": \"Jane Cook\" }", "Jane Cook")]
    [InlineData("[\"Jane Cook\", { \"@type\": \"Person\", \"name\": \"Alex Baker\" }]", "Jane Cook, Alex Baker")]
    [InlineData("[\"Only\"]", "Only")]
    public void Parse_handles_author_variants(string authorJson, string expected)
    {
        var html = Page($$"""{ "@type": "Recipe", "name": "Authored", "author": {{authorJson}} }""");

        StructuredRecipeParser.Parse(html, PageUrl).Single().Author.Should().Be(expected);
    }

    [Theory]
    [InlineData("PT20M", 20)]
    [InlineData("PT1H", 60)]
    [InlineData("PT1H30M", 90)]
    [InlineData("PT45M", 45)]
    [InlineData("PT90S", 1)]
    [InlineData("PT0M", 0)]
    [InlineData("P1D", 1440)]
    [InlineData("P1DT2H", 1440)]
    [InlineData("P1W", 1440)]
    [InlineData("pt1h5m", 65)]
    [InlineData("PT1H1H", null)]
    [InlineData("PT1M1M", null)]
    [InlineData("PT1S1S", null)]
    [InlineData("P1W1W", null)]
    [InlineData("P1D1D", null)]
    [InlineData("PT9223372036854775807H", 1440)]
    [InlineData("PT99999999999999999999999H", null)]
    [InlineData("P1Y", null)]
    [InlineData("P1M", null)]
    [InlineData("P", null)]
    [InlineData("PT", null)]
    [InlineData("P1DT", null)]
    [InlineData("PT1.5H", null)]
    [InlineData("-PT20M", null)]
    [InlineData("20M", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryParseIso8601Minutes_parses_supported_durations(string? value, int? expected)
    {
        StructuredRecipeParser.TryParseIso8601Minutes(value, out var minutes).Should().Be(expected is not null);
        minutes.Should().Be(expected ?? 0);
    }

    [Fact]
    public void Parse_caps_durations_at_the_m3_maximum()
    {
        var html = Page("""{ "@type": "Recipe", "name": "Slow", "prepTime": "PT99999M" }""");

        StructuredRecipeParser.Parse(html, PageUrl).Single().PrepMinutes.Should().Be(1440);
    }

    [Fact]
    public void Parse_skips_malformed_block_and_keeps_a_valid_block()
    {
        var html = """
            <html><head>
            <script type="application/ld+json">{ this is not valid JSON </script>
            <script type="application/ld+json">
            {"@type": "Recipe", "name": "Valid"}
            </script>
            </head></html>
            """;

        StructuredRecipeParser.Parse(html, PageUrl).Select(r => r.Name).Should().Equal("Valid");
    }

    [Fact]
    public void Parse_skips_truncated_json_without_throwing()
    {
        var html = """
            <script type="application/ld+json">{"@type":"Recipe","name":"Broken",</script>
            """;

        FluentActions.Invoking(() => StructuredRecipeParser.Parse(html, PageUrl))
            .Should().NotThrow();
        StructuredRecipeParser.Parse(html, PageUrl).Should().BeEmpty();
    }

    [Fact]
    public void Parse_decodes_entities_and_strips_markup()
    {
        var html = Page(
            """
            {
              "@type": "Recipe",
              "name": "Entities",
              "description": "<p>A &amp; B &#39;quoted&#39; &lt;b&gt;bold&lt;/b&gt;</p>"
            }
            """);

        StructuredRecipeParser.Parse(html, PageUrl).Single().Description.Should().Be("A & B 'quoted' bold");
    }

    [Fact]
    public void Parse_decodes_entities_inside_ingredient_and_instruction_text()
    {
        var html = Page(
            """
            {
              "@type": "Recipe",
              "name": "Entities",
              "recipeIngredient": ["Salt &amp; pepper <i>to taste</i>"],
              "recipeInstructions": [ { "@type": "HowToStep", "text": "Stir &amp; serve." } ]
            }
            """);

        var recipe = StructuredRecipeParser.Parse(html, PageUrl).Single();

        recipe.IngredientLines.Should().Equal("Salt & pepper to taste");
        recipe.InstructionSections.Single().Steps.Should().Equal("Stir & serve.");
    }

    [Fact]
    public void Parse_returns_empty_for_a_page_without_json_ld()
    {
        var html = "<!doctype html><html><head><title>Nothing</title></head><body>Hello</body></html>";

        StructuredRecipeParser.Parse(html, PageUrl).Should().BeEmpty();
    }

    [Fact]
    public void Parse_returns_empty_for_null_or_blank_html()
    {
        StructuredRecipeParser.Parse(null).Should().BeEmpty();
        StructuredRecipeParser.Parse(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Parse_handles_script_attributes_and_content_types()
    {
        var html = """
            <SCRIPT id="x" type='application/ld+json; charset=utf-8' data-note="a>b">
            {"@type":"Recipe","name":"Styled"}
            </SCRIPT>
            """;

        StructuredRecipeParser.Parse(html, PageUrl).Single().Name.Should().Be("Styled");
    }

    [Fact]
    public void Parse_ignores_scripts_that_are_not_json_ld()
    {
        var html = Page("""{ "@type": "Recipe", "name": "Nope" }""", "text/javascript");

        StructuredRecipeParser.Parse(html, PageUrl).Should().BeEmpty();
    }

    [Fact]
    public void Parse_finds_a_recipe_wrapped_in_main_entity_and_item_list()
    {
        var html = Page(
            """
            {
              "@type": "ItemList",
              "itemListElement": [
                { "@type": "ListItem", "item": { "@type": "Recipe", "name": "Wrapped" } }
              ],
              "mainEntity": { "@type": "Recipe", "name": "Main" }
            }
            """);

        StructuredRecipeParser.Parse(html, PageUrl).Select(r => r.Name).Should().Equal("Wrapped", "Main");
    }

    [Fact]
    public void Parse_handles_deep_but_legal_nesting_without_throwing()
    {
        var depth = 120;
        var nested = string.Concat(Enumerable.Repeat("[", depth))
            + "\"x\""
            + string.Concat(Enumerable.Repeat("]", depth));
        var html = Page($$"""{ "@type": "Recipe", "name": "Deep", "description": {{nested}} }""");

        var recipe = StructuredRecipeParser.Parse(html, PageUrl).Single();

        recipe.Description.Should().BeNull();
    }

    [Fact]
    public void Parse_does_not_throw_on_excessive_nesting_and_oversized_values()
    {
        var tooDeep = string.Concat(Enumerable.Repeat("[", 600))
            + "1"
            + string.Concat(Enumerable.Repeat("]", 600));
        var deepPage = Page($$"""{ "@type": "Recipe", "name": "Deep", "description": {{tooDeep}} }""");
        var hugeName = new string('x', 100_000);
        var hugePage = Page($$"""{ "@type": "Recipe", "name": "{{hugeName}}" }""");

        FluentActions.Invoking(() => StructuredRecipeParser.Parse(deepPage, PageUrl)).Should().NotThrow();
        var huge = StructuredRecipeParser.Parse(hugePage, PageUrl);
        huge.Should().ContainSingle();
        huge[0].Name.Should().Be(hugeName);
    }

    [Fact]
    public void Parse_does_not_run_javascript_in_the_markup()
    {
        // The literal text of a script must never be treated as a recipe source.
        var html = """
            <script type="text/javascript">
            document.write('{"@type":"Recipe","name":"Injected"}');
            </script>
            """;

        StructuredRecipeParser.Parse(html, PageUrl).Should().BeEmpty();
    }

    [Fact]
    public void Parse_leaves_source_url_null_when_caller_does_not_supply_one()
    {
        var html = Page("""{ "@type": "Recipe", "name": "No source" }""");

        StructuredRecipeParser.Parse(html).Single().SourceUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public void Parse_never_throws_on_lone_surrogate_escapes_in_any_field(string escape)
    {
        var bodies = new[]
        {
            $$"""{ "@type": "Recipe", "name": "x{{escape}}y" }""",
            $$"""{ "@type": "Recipe{{escape}}", "name": "x" }""",
            $$"""{ "@type": "Recipe", "name": "x", "description": "d{{escape}}e" }""",
            $$"""{ "@type": "Recipe", "name": "x", "author": "a{{escape}}b" }""",
            $$"""{ "@type": "Recipe", "name": "x", "recipeIngredient": ["i{{escape}}j"] }""",
            $$"""{ "@type": "Recipe", "name": "x", "recipeInstructions": [ { "@type": "HowToStep", "text": "s{{escape}}t" } ] }""",
            $$"""{ "@type": "Recipe", "name": "x", "image": "https://example.com/a{{escape}}.jpg" }""",
        };

        foreach (var body in bodies)
        {
            var html = Page(body);

            FluentActions.Invoking(() => StructuredRecipeParser.Parse(html, PageUrl)).Should().NotThrow();
        }
    }

    [Fact]
    public void Parse_treats_a_lone_surrogate_value_as_absent_but_keeps_the_recipe()
    {
        var html = Page(
            """{ "@type": "Recipe", "name": "x\uD800y", "recipeInstructions": [ { "@type": "HowToStep", "text": "Step." } ] }""");

        var recipe = StructuredRecipeParser.Parse(html, PageUrl).Single();

        recipe.Name.Should().BeNull();
        recipe.InstructionSections.Single().Steps.Should().Equal("Step.");
    }

    [Theory]
    [InlineData("a</scriptx>b")]
    [InlineData("a</scriptb")]
    public void Parse_requires_a_delimiter_after_the_script_end_tag(string description)
    {
        var html = Page($$"""{ "@type": "Recipe", "name": "Delimited", "description": "{{description}}" }""");

        StructuredRecipeParser.Parse(html, PageUrl).Should().ContainSingle()
            .Which.Name.Should().Be("Delimited");
    }

    [Fact]
    public void Parse_ignores_a_fake_json_ld_tag_inside_a_non_json_ld_script()
    {
        var html = """
            <script type="text/javascript">
            var x = '<script type="application/ld+json">{"@type":"Recipe","name":"Fake"}</script>';
            </script>
            <script type="application/ld+json">{"@type":"Recipe","name":"Real"}</script>
            """;

        StructuredRecipeParser.Parse(html, PageUrl).Select(r => r.Name).Should().Equal("Real");
    }

    [Fact]
    public void Parse_ignores_a_script_inside_an_html_comment()
    {
        var html = """
            <!-- <script type="application/ld+json">{"@type":"Recipe","name":"Commented"}</script> -->
            <script type="application/ld+json">{"@type":"Recipe","name":"Visible"}</script>
            """;

        StructuredRecipeParser.Parse(html, PageUrl).Select(r => r.Name).Should().Equal("Visible");
    }

    [Fact]
    public void Parse_omits_how_to_sections_with_no_title_and_no_steps()
    {
        var html = Page(
            """
            {
              "@type": "Recipe",
              "name": "Sections",
              "recipeInstructions": [
                { "@type": "HowToSection" },
                { "@type": "HowToSection", "name": "Titled but empty" },
                {
                  "@type": "HowToSection",
                  "name": "Real",
                  "itemListElement": [ { "@type": "HowToStep", "text": "Do it." } ]
                }
              ]
            }
            """);

        var sections = StructuredRecipeParser.Parse(html, PageUrl).Single().InstructionSections;

        sections.Select(section => section.Title).Should().Equal("Titled but empty", "Real");
    }
}
