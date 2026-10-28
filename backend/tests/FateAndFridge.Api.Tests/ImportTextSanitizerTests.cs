using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Untrusted imported-text sanitization.
/// </summary>
public sealed class ImportTextSanitizerTests
{
    [Theory]
    [InlineData("Fish &amp; chips", "Fish & chips")]
    [InlineData("1 &lt; 2 &gt; 0", "1 < 2 > 0")]
    [InlineData("It&#39;s ready", "It's ready")]
    [InlineData("A&nbsp;B", "A B")]
    public void Sanitize_decodes_html_entities(string input, string expected)
    {
        ImportTextSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("<p>Hello <b>world</b></p>", "Hello world")]
    [InlineData("Line<br/>break", "Line break")]
    [InlineData("<div><span>Nested</span></div>", "Nested")]
    [InlineData("<script>alert(1)</script>", "alert(1)")]
    [InlineData("&lt;script&gt;alert(1)&lt;/script&gt;", "alert(1)")]
    public void Sanitize_strips_markup_and_never_returns_html(string input, string expected)
    {
        ImportTextSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_removes_control_and_format_characters()
    {
        ImportTextSanitizer.Sanitize("a\u0000b\u0007c\u200Bd").Should().Be("abcd");
    }

    [Fact]
    public void Sanitize_collapses_whitespace_and_trims()
    {
        ImportTextSanitizer.Sanitize("  first\t\tsecond \n third  ").Should().Be("first second third");
    }

    [Fact]
    public void Sanitize_normalizes_carriage_returns()
    {
        ImportTextSanitizer.Sanitize("a\r\nb\rc").Should().Be("a b c");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<b></b>")]
    [InlineData("&nbsp;")]
    [InlineData("\u0001\u0002")]
    public void Sanitize_returns_null_for_empty_results(string? input)
    {
        ImportTextSanitizer.Sanitize(input).Should().BeNull();
    }

    [Fact]
    public void Sanitize_truncates_to_the_optional_limit()
    {
        ImportTextSanitizer.Sanitize("abcdef", 3).Should().Be("abc");
        ImportTextSanitizer.Sanitize("ab", 200).Should().Be("ab");
        ImportTextSanitizer.Sanitize("abcdef", -1).Should().Be("abcdef");
    }

    [Theory]
    [InlineData("\t\n", false)]
    [InlineData("clean text", false)]
    [InlineData("bell\u0007", true)]
    [InlineData("zero-width\u200B", true)]
    [InlineData(null, false)]
    public void ContainsDisallowedControlCharacter_reports_control_and_format_characters(
        string? input,
        bool expected)
    {
        ImportTextSanitizer.ContainsDisallowedControlCharacter(input).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_is_idempotent()
    {
        var once = ImportTextSanitizer.Sanitize("  <p>A &amp; B</p>  ");

        ImportTextSanitizer.Sanitize(once).Should().Be(once);
    }

    [Fact]
    public void Sanitize_removes_bidi_override_format_characters()
    {
        ImportTextSanitizer.Sanitize("abc\u202Edef").Should().Be("abcdef");
    }

    [Fact]
    public void Sanitize_drops_unpaired_surrogates_and_keeps_paired_ones()
    {
        ImportTextSanitizer.Sanitize("a\uD800b").Should().Be("ab");
        ImportTextSanitizer.Sanitize("a\uDC00b").Should().Be("ab");
        ImportTextSanitizer.Sanitize("a\uD83D\uDE00b").Should().Be("a\uD83D\uDE00b");
    }

    [Fact]
    public void ContainsDisallowedControlCharacter_reports_unpaired_surrogates()
    {
        ImportTextSanitizer.ContainsDisallowedControlCharacter("lone high\uD800").Should().BeTrue();
        ImportTextSanitizer.ContainsDisallowedControlCharacter("lone low\uDC00").Should().BeTrue();
    }

    [Fact]
    public void ContainsDisallowedControlCharacter_allows_paired_surrogates()
    {
        ImportTextSanitizer.ContainsDisallowedControlCharacter("emoji \uD83D\uDE00").Should().BeFalse();
    }
}
