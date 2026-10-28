namespace FateAndFridge.Api.Imports;

/// <summary>
/// Draft returned by <c>POST /api/v1/import/recipe-url</c>. It is a pure response shape: no database row is
/// created or changed while it is produced.
/// </summary>
/// <param name="Source">Provenance of the fetched page.</param>
/// <param name="SourceUrlMatch">
/// Existing recipe whose normalized source URL equals the page URL, or null
/// (owns the full duplicate workflow; exposes the exact match).
/// </param>
/// <param name="Capabilities">What the acting user may do during review.</param>
/// <param name="Recipes">One entry per parsed candidate; always at least one on success.</param>
public sealed record ImportDraftResponse(
    ImportDraftSource Source,
    ImportDraftReference? SourceUrlMatch,
    ImportDraftCapabilities Capabilities,
    IReadOnlyList<ImportDraftRecipe> Recipes);

/// <summary>Source block of an import draft.</summary>
/// <param name="Url">Absolute URL the draft was parsed from (after redirects).</param>
/// <param name="Site">Host of <paramref name="Url"/>.</param>
/// <param name="PageTitle">Page title; null until enrichment lands in a later M4 issue.</param>
/// <param name="Author">Author reported by the parsed candidate, when present.</param>
/// <param name="FetchedAt">UTC timestamp of the successful fetch.</param>
/// <param name="ParseMethod"><c>json_ld</c> or <c>html</c>.</param>
public sealed record ImportDraftSource(
    string Url,
    string? Site,
    string? PageTitle,
    string? Author,
    DateTimeOffset FetchedAt,
    string ParseMethod);

/// <summary>Minimal <c>{ id, name }</c> reference used by duplicate/name diagnostics.</summary>
public sealed record ImportDraftReference(long Id, string Name);

/// <summary>Capabilities of the acting user, so the UI can hide create affordances.</summary>
public sealed record ImportDraftCapabilities(bool CanCreateReferenceData);

/// <summary>One parsed recipe candidate in an import draft.</summary>
public sealed record ImportDraftRecipe(
    int Index,
    string Name,
    string? Description,
    string? Author,
    string? SourceUrl,
    string? SourceSite,
    int? PrepMinutes,
    int? CookMinutes,
    string? Notes,
    string? ImageSourceUrl,
    IReadOnlyList<ImportDraftReference> NameMatches,
    IReadOnlyList<ImportDraftIngredient> Ingredients,
    IReadOnlyList<ImportDraftInstructionSection> InstructionSections,
    IReadOnlyList<ImportDraftReference> Proteins,
    IReadOnlyList<ImportDraftReference> Tags,
    IReadOnlyList<string> Warnings);

/// <summary>
/// One raw ingredient line in a draft. The quantity, name, preparation note
/// and unit slice are populated by <c>IngredientLineParser</c>/<c>ImportNormalizer</c>
/// canonical ingredient matching (<see cref="MatchedIngredientId"/>
/// <see cref="MatchKind"/>, and ingredient suggestions) is populated by
/// <c>IngredientMatcher</c>. Unknown lines keep safe defaults (null match
/// <c>IsUnknown = true</c>) until the review screen resolves them.
/// </summary>
public sealed record ImportDraftIngredient(
    string Raw,
    decimal? Quantity,
    string? Name,
    string? PreparationNote,
    long? MatchedIngredientId,
    string? MatchKind,
    bool IsUnknown,
    IReadOnlyList<ImportDraftReference> Suggestions,
    ImportDraftUnit Unit);

/// <summary>Unit slice of a draft ingredient.</summary>
public sealed record ImportDraftUnit(
    string? Raw,
    long? MatchedUnitId,
    string? MatchedSymbol,
    bool IsUnknown,
    string? SuggestedDimension,
    IReadOnlyList<ImportDraftUnitSuggestion> Suggestions);

/// <summary>Unit candidate offered by the review screen.</summary>
public sealed record ImportDraftUnitSuggestion(long Id, string Symbol, string DisplayName);

/// <summary>Instruction section of a draft candidate.</summary>
public sealed record ImportDraftInstructionSection(string? Title, IReadOnlyList<string> Steps);

/// <summary>Response of <c>POST /api/v1/import/check</c>.</summary>
public sealed record ImportDraftCheckResponse(
    ImportDraftReference? SourceUrlMatch,
    IReadOnlyList<ImportDraftReference> NameMatches);
