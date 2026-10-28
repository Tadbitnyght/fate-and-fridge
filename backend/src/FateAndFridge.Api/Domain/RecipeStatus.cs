namespace FateAndFridge.Api.Domain;

/// <summary>
/// Recipe lifecycle status (docs/DATABASE.md §3). Stored as snake_case text
/// <c>normal</c>, <c>manual_only</c>, or <c>retired</c>. Retired recipes remain
/// browsable and resolvable for existing data but are excluded from
/// randomization and new reference selection.
/// </summary>
public enum RecipeStatus
{
    Normal,
    ManualOnly,
    Retired,
}
