namespace FateAndFridge.Api.Services;

/// <summary>
/// The normalized comparison key for staple de-duplication (docs/M8_PLAN.md
/// §1.8, §1.9): the reference-data normalized form when the name is
/// normalizable, else a trimmed invariant-lowercase fallback so repeated
/// promotion or creation of the same staple stays idempotent.
/// </summary>
internal static class GroceryStapleName
{
    /// <summary>Canonical de-duplication key for a staple name.</summary>
    public static string Key(string name) =>
        ReferenceDataNormalizer.Normalize(name) is ReferenceDataNormalizationResult.Success success
            ? success.Normalized
            : name.Trim().ToLowerInvariant();
}
