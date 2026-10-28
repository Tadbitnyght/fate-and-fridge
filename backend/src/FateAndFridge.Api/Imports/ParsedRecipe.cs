namespace FateAndFridge.Api.Imports;

/// <summary>
/// One titled group of instruction steps in a parsed recipe
/// A <c>null</c>
/// <see cref="Title"/> is the untitled group produced by plain strings or bare
/// <c>HowToStep</c> objects.
/// </summary>
public sealed record ParsedInstructionSection(string? Title, IReadOnlyList<string> Steps);

/// <summary>
/// Parser-agnostic intermediate recipe produced by both import parsers
/// (<c>StructuredRecipeParser</c> and, later, <c>HtmlFallbackParser</c>) and
/// consumed by the import draft pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately carries no EF Core, HTTP, or database concerns. Every
/// text value has already passed through <see cref="ImportTextSanitizer"/>, so
/// it is plain text with markup stripped, HTML entities decoded, control
/// characters removed, and whitespace collapsed. A value that could not be
/// parsed unambiguously is <c>null</c> (or omitted from a list) — parsers never
/// invent data.
/// </para>
/// <para>
/// Length limits are concerns and are not enforced here.
/// </para>
/// </remarks>
public sealed record ParsedRecipe
{
    /// <summary>Recipe display name, or <c>null</c> when the source omitted it.</summary>
    public string? Name { get; init; }

    /// <summary>Recipe description as plain text.</summary>
    public string? Description { get; init; }

    /// <summary>Author name(s), joined with <c>", "</c> when the source listed several.</summary>
    public string? Author { get; init; }

    /// <summary>The page URL the recipe was parsed from, when the caller supplied one.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Resolved image URL for optional client-side preview; downloaded later.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Preparation time in whole minutes, or <c>null</c> when absent or ambiguous.</summary>
    public int? PrepMinutes { get; init; }

    /// <summary>Cook time in whole minutes, or <c>null</c> when absent or ambiguous.</summary>
    public int? CookMinutes { get; init; }

    /// <summary>Raw ingredient lines in source order, one line per ingredient.</summary>
    public IReadOnlyList<string> IngredientLines { get; init; } = [];

    /// <summary>Instruction sections in source order; empty when the source omitted instructions.</summary>
    public IReadOnlyList<ParsedInstructionSection> InstructionSections { get; init; } = [];

    /// <summary>Free-form notes; currently never populated by the JSON-LD parser.</summary>
    public string? Notes { get; init; }
}
