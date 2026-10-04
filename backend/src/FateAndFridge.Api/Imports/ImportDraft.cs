namespace FateAndFridge.Api.Imports;

/// <summary>
/// Draft returned by <c>POST /api/v1/import/recipe-url</c> (docs/M4_PLAN.md
/// §2.2, docs/API.md §15). It is a pure response shape: no database row is
/// created or changed while it is produced.
/// </summary>
/// <param name="Source">Provenance of the fetched page.</param>
/// <param name="SourceUrlMatch">
/// Existing recipe whose normalized source URL equals the page URL, or null
/// (M4-11 owns the full duplicate workflow; M4-04 exposes the exact match).
/// </param>
/// <param name="Capabilities">What the acting user may do during review.</param>
/// <param name="Recipes">One entry per parsed candidate; always at least one on success.</param>
public sealed record ImportDraftResponse(
    ImportDraftSource Source,
    ImportDraftReference? SourceUrlMatch,
    ImportDraftCapabilities Capabilities,
    IReadOnlyList<ImportDraftRecipe> Recipes);

/// <summary>Source block of an import draft (docs/M4_PLAN.md §2.2).</summary>
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

/// <summary>Capabilities of the acting user, so the UI can hide create affordances (docs/M4_PLAN.md §1.3).</summary>
public sealed record ImportDraftCapabilities(bool CanCreateReferenceData);

/// <summary>One parsed recipe candidate in an import draft (docs/M4_PLAN.md §2.2).</summary>
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
/// One raw ingredient line in a draft. M4-04 carries only <see cref="Raw"/>;
/// M4-05 splits quantity/unit/name/note and M4-06 resolves matches, so every
/// enrichment field keeps its safe default here.
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

/// <summary>Unit slice of a draft ingredient (docs/M4_PLAN.md §2.2).</summary>
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

/// <summary>Response of <c>POST /api/v1/import/check</c> (docs/M4_PLAN.md §2.2).</summary>
public sealed record ImportDraftCheckResponse(
    ImportDraftReference? SourceUrlMatch,
    IReadOnlyList<ImportDraftReference> NameMatches);
