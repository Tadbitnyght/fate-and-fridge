using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pre-parse budget guard for the HTML fallback.
/// The estimator must be cheap, total, and conservative: it only decides
/// whether <see cref="HtmlFallbackParser"/> runs, never whether JSON-LD runs.
/// </summary>
public sealed class HtmlDepthGuardTests
{
    [Fact]
    public void Shallow_document_is_within_budget()
    {
        const string html = "<html><head><title>x</title></head><body><div><p>hi</p></div></body></html>";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(4);
        HtmlDepthGuard.IsWithinDepthBudget(html).Should().BeTrue();
    }

    [Fact]
    public void Depth_at_the_limit_is_allowed_and_one_past_is_not()
    {
        HtmlDepthGuard.EstimateMaxTagDepth(Repeat("<div>", HtmlDepthGuard.MaxSafeTagDepth))
            .Should().Be(HtmlDepthGuard.MaxSafeTagDepth);
        HtmlDepthGuard.IsWithinDepthBudget(Repeat("<div>", HtmlDepthGuard.MaxSafeTagDepth)).Should().BeTrue();

        var onePast = Repeat("<div>", HtmlDepthGuard.MaxSafeTagDepth + 1);
        HtmlDepthGuard.EstimateMaxTagDepth(onePast).Should().Be(HtmlDepthGuard.MaxSafeTagDepth + 1);
        HtmlDepthGuard.IsWithinDepthBudget(onePast).Should().BeFalse();
    }

    [Fact]
    public void Void_and_self_closing_tags_do_not_open_a_level()
    {
        const string html = "<div><br><img src=\"x\"><hr/><input><meta charset=\"utf-8\"></div>";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(1);
    }

    [Fact]
    public void Comments_doctypes_and_processing_instructions_are_ignored()
    {
        const string html = "<!DOCTYPE html><!-- <div><div><div> --><?xml version=\"1.0\"?><section><p>x</p></section>";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(2);
    }

    [Fact]
    public void Closing_tags_decrement_depth()
    {
        const string html = "<a><b><c></c></b></a><d>";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(3);
    }

    [Fact]
    public void Truncated_markup_is_tolerated_and_never_throws()
    {
        const string html = "<div><span><b";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(2);
        HtmlDepthGuard.IsWithinDepthBudget(html).Should().BeTrue();
        HtmlDepthGuard.EstimateMaxTagDepth(null).Should().Be(0);
        HtmlDepthGuard.EstimateMaxTagDepth(string.Empty).Should().Be(0);
    }

    [Fact]
    public void Deep_page_still_yields_the_json_ld_recipe_because_json_ld_is_guarded_separately()
    {
        var html = string.Concat(
            Repeat("<div>", HtmlDepthGuard.MaxSafeTagDepth + 10),
            "<script type=\"application/ld+json\">",
            """{"@context":"https://schema.org","@type":"Recipe","name":"Deep Lasagna"}""",
            "</script>");

        HtmlDepthGuard.IsWithinDepthBudget(html).Should().BeFalse(
            "the fallback must be skipped for absurd nesting");

        var parsed = StructuredRecipeParser.Parse(html, "https://example.com/deep");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Deep Lasagna");
    }

    [Theory]
    [InlineData("script")]
    [InlineData("style")]
    [InlineData("title")]
    [InlineData("textarea")]
    public void Raw_text_element_contents_are_not_counted(string tag)
    {
        var html = $"<div><{tag}>var x = \"<div><div><div>\";</{tag}></div>";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(2);
    }

    [Fact]
    public void Raw_text_element_without_a_closing_tag_stops_safely()
    {
        const string html = "<div><script>var x = \"<div><div>\";";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(2);
    }

    [Fact]
    public void Less_than_signs_that_do_not_start_a_tag_are_literal_text()
    {
        const string html = "<p>1 < 2 and 3 <= 4 and <3 and < b</p>";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(1);
    }

    [Fact]
    public void Greater_than_inside_quoted_attribute_values_does_not_end_the_tag()
    {
        const string html = "<div title=\"<a><b><c>\"><p></p></div>";

        HtmlDepthGuard.EstimateMaxTagDepth(html).Should().Be(2);
    }

    private static string Repeat(string value, int count) =>
        string.Concat(Enumerable.Repeat(value, count));
}
