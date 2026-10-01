using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Adversarial matrix for the M2-03 reference-data normalization rule
/// (docs/DATABASE.md §1, docs/M2_PLAN.md §1.4): case, diacritics, punctuation,
/// whitespace, control characters, long input, and determinism/idempotence.
/// </summary>
public sealed class ReferenceDataNormalizerTests
{
    [Theory]
    [InlineData("Salt", "Salt", "salt")]
    [InlineData("SALT", "SALT", "salt")]
    [InlineData("salt", "salt", "salt")]
    [InlineData("  Salt  ", "Salt", "salt")]
    public void Normalize_lowercases_ascii_and_keeps_the_display_form(string input, string display, string normalized) =>
        AssertSuccess(input, display, normalized);

    [Theory]
    [InlineData("ПОМИДОР", "помидор")]
    [InlineData("Помидор", "помидор")]
    [InlineData("ΝΤΟΜΑΤΑ", "ντοματα")]
    [InlineData("Σόφια", "σοφια")]
    [InlineData("ΆΝΩ", "ανω")]
    [InlineData("ϊϋ", "ιυ")]
    [InlineData("ΠΟΣ", "ποσ")]
    [InlineData("πος", "ποσ")]
    [InlineData("ϓ", "υ")]
    [InlineData("ϔ", "υ")]
    [InlineData("ΑΒΓ", "αβγ")]
    public void Normalize_lowercases_non_latin_scripts_without_transliterating(string input, string normalized)
    {
        var success = AsSuccess(input);

        success.Display.Should().Be(input);
        success.Normalized.Should().Be(normalized);
    }

    [Theory]
    [InlineData("Jalapeño", "jalapeno")]
    [InlineData("jalapeño", "jalapeno")]
    [InlineData("JALAPEÑO", "jalapeno")]
    [InlineData("Crème Brûlée", "creme brulee")]
    [InlineData("Ärger", "arger")]
    [InlineData("Łódź", "lodz")]
    [InlineData("Ærø", "aero")]
    [InlineData("Straße", "strasse")]
    public void Normalize_folds_diacritics(string input, string normalized)
    {
        var success = AsSuccess(input);

        success.Display.Should().Be(input);
        success.Normalized.Should().Be(normalized);
    }

    [Fact]
    public void Normalize_folds_composed_and_decomposed_unicode_to_the_same_normalized_form()
    {
        const string composed = "Jalapeño";
        const string decomposed = "Jalapen\u0303o";
        composed.Should().NotBe(decomposed, "the two inputs must exercise different Unicode forms");

        var fromComposed = AsSuccess(composed);
        var fromDecomposed = AsSuccess(decomposed);

        fromComposed.Normalized.Should().Be("jalapeno");
        fromDecomposed.Normalized.Should().Be("jalapeno");
        fromComposed.Display.Should().Be(composed);
        fromDecomposed.Display.Should().Be(decomposed);
    }

    [Fact]
    public void Normalize_strips_combining_marks_from_decomposed_input()
    {
        const string decomposed = "jalapen\u0303o";

        var success = AsSuccess(decomposed);

        success.Display.Should().Be(decomposed);
        success.Normalized.Should().Be("jalapeno");
    }

    [Fact]
    public void Normalize_folds_n_preceded_by_apostrophe_to_its_decomposed_form()
    {
        var success = AsSuccess("\u0149");

        success.Display.Should().Be("\u0149");
        success.Normalized.Should().Be("\u02BCn");
        success.Normalized.Should().NotBe("n");
        AsSuccess("\u02BCn").Normalized.Should().Be("\u02BCn");
    }

    [Theory]
    [InlineData("कु", "क")]
    [InlineData("بَيْت", "بيت")]
    public void Normalize_strips_non_spacing_marks_in_mark_based_scripts(string input, string normalized)
    {
        var success = AsSuccess(input);

        success.Display.Should().Be(input);
        success.Normalized.Should().Be(normalized);
    }

    [Theory]
    [InlineData("fl. oz.", "fl. oz.")]
    [InlineData("FL. OZ.", "fl. oz.")]
    [InlineData("Meat & Seafood", "meat & seafood")]
    [InlineData("tomato (large)", "tomato (large)")]
    [InlineData("t", "t")]
    [InlineData("T", "t")]
    public void Normalize_preserves_punctuation(string input, string normalized) =>
        AssertSuccess(input, input, normalized);

    [Theory]
    [InlineData("Jalapeño  Pepper", "Jalapeño Pepper", "jalapeno pepper")]
    [InlineData("Jalapeño\t Pepper", "Jalapeño Pepper", "jalapeno pepper")]
    [InlineData("  Jalapeño\nPepper  ", "Jalapeño Pepper", "jalapeno pepper")]
    [InlineData("Jalapeño\r\nPepper", "Jalapeño Pepper", "jalapeno pepper")]
    [InlineData("Crème\u00A0Brûlée", "Crème Brûlée", "creme brulee")]
    [InlineData("a \t\r\n b", "a b", "a b")]
    [InlineData("a\u3000b", "a b", "a b")]
    [InlineData("\tJalapeño\n", "Jalapeño", "jalapeno")]
    public void Normalize_trims_and_collapses_whitespace_runs(string input, string display, string normalized) =>
        AssertSuccess(input, display, normalized);

    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u0007")]
    [InlineData("\u000B")]
    [InlineData("\u001B")]
    [InlineData("\u007F")]
    [InlineData("\u0085")]
    [InlineData("\u200B")]
    [InlineData("\uFEFF")]
    [InlineData("\r")]
    [InlineData("a\rb")]
    [InlineData("Jalapeño\u0007Pepper")]
    [InlineData("salt\u200B")]
    public void Normalize_rejects_control_and_format_characters(string input) =>
        AssertInvalid(input, ReferenceDataNormalizationFailureReason.ControlCharacters);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\u00A0")]
    [InlineData(" \t\n ")]
    public void Normalize_rejects_null_blank_and_whitespace_only_input(string? input) =>
        AssertInvalid(input, ReferenceDataNormalizationFailureReason.Empty);

    [Fact]
    public void Normalize_rejects_input_with_no_normalized_form_after_folding() =>
        AssertInvalid("\u0301", ReferenceDataNormalizationFailureReason.Empty);

    [Theory]
    [InlineData("\u0301 \u0302")]
    [InlineData("\u0301 \u0302 \u0303")]
    public void Normalize_rejects_mark_only_input_that_folds_away_to_whitespace(string input) =>
        AssertInvalid(input, ReferenceDataNormalizationFailureReason.Empty);

    [Fact]
    public void Normalize_recollapses_whitespace_exposed_by_folding()
    {
        const string input = "x\u0301 \u0302 y";

        var success = AsSuccess(input);

        success.Display.Should().Be(input);
        success.Normalized.Should().Be("x y");

        var renormalized = AsSuccess(success.Normalized);

        renormalized.Display.Should().Be(success.Normalized);
        renormalized.Normalized.Should().Be(success.Normalized);
    }

    [Fact]
    public void Normalize_handles_ten_thousand_ascii_characters_deterministically()
    {
        var input = new string('A', 10_000);

        var first = AsSuccess(input);
        var second = AsSuccess(input);

        first.Display.Should().Be(input);
        first.Normalized.Should().Be(new string('a', 10_000));
        second.Should().Be(first);
    }

    [Fact]
    public void Normalize_handles_a_long_mixed_input_and_stays_idempotent()
    {
        const int chunks = 1_250;
        var input = string.Concat(Enumerable.Repeat("Jalapeño\t", chunks));
        var expectedDisplay = string.Join(' ', Enumerable.Repeat("Jalapeño", chunks));
        var expectedNormalized = string.Join(' ', Enumerable.Repeat("jalapeno", chunks));

        var result = AsSuccess(input);

        result.Display.Should().Be(expectedDisplay);
        result.Normalized.Should().Be(expectedNormalized);

        var renormalized = AsSuccess(result.Normalized);

        renormalized.Display.Should().Be(result.Normalized);
        renormalized.Normalized.Should().Be(result.Normalized);
    }

    [Theory]
    [InlineData("Jalapeño  Pepper ")]
    [InlineData("Crème Brûlée")]
    [InlineData("fl. oz.")]
    [InlineData("ПОМИДОР")]
    [InlineData("Meat & Seafood")]
    [InlineData("Tomatoes")]
    [InlineData("JALAPEÑO\tPEPPER")]
    public void Normalize_is_idempotent(string input)
    {
        var first = AsSuccess(input);
        var second = AsSuccess(first.Normalized);

        second.Display.Should().Be(first.Normalized);
        second.Normalized.Should().Be(first.Normalized);
        AsSuccess(first.Display).Normalized.Should().Be(first.Normalized);
    }

    [Theory]
    [InlineData("Jalapeño  Pepper ")]
    [InlineData("Crème Brûlée")]
    [InlineData("ПОМИДОР")]
    [InlineData("fl. oz.")]
    public void Normalize_is_deterministic_across_repeated_calls(string input) =>
        AsSuccess(input).Should().Be(AsSuccess(input));

    [Theory]
    [InlineData("Tomatoes", "tomatoes")]
    [InlineData("TOMATOES", "tomatoes")]
    [InlineData("Berries", "berries")]
    [InlineData("Potatoes", "potatoes")]
    public void Normalize_does_not_fold_plurals(string input, string normalized)
    {
        var success = AsSuccess(input);

        success.Display.Should().Be(input);
        success.Normalized.Should().Be(normalized);
    }

    [Fact]
    public void Normalize_keeps_tomatoes_distinct_from_tomato()
    {
        AsSuccess("Tomatoes").Normalized.Should().Be("tomatoes");
        AsSuccess("Tomato").Normalized.Should().Be("tomato");
        AsSuccess("Tomatoes").Normalized.Should().NotBe(AsSuccess("Tomato").Normalized);
    }

    private static void AssertSuccess(string input, string display, string normalized)
    {
        var success = AsSuccess(input);

        success.Display.Should().Be(display);
        success.Normalized.Should().Be(normalized);
    }

    private static void AssertInvalid(string? input, ReferenceDataNormalizationFailureReason reason) =>
        ReferenceDataNormalizer.Normalize(input)
            .Should().BeOfType<ReferenceDataNormalizationResult.Invalid>()
            .Which.Reason.Should().Be(reason);

    private static ReferenceDataNormalizationResult.Success AsSuccess(string input) =>
        ReferenceDataNormalizer.Normalize(input)
            .Should().BeOfType<ReferenceDataNormalizationResult.Success>()
            .Which;
}
