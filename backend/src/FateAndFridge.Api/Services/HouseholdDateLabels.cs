using System.Globalization;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The household-local date labels shared by the statistics and meal-history
/// reads. The month names are table-driven so a label
/// is identical in every deployment: the host may run with globalization
/// invariant mode, where ICU-backed names are empty and
/// `CultureInfo`-derived month names are unusable.
/// </summary>
internal static class HouseholdDateLabels
{
    /// <summary>
    /// English month names in calendar order, table-driven so the labels are
    /// deterministic under globalization-invariant mode (invariant ICU is
    /// disabled on the deployment host).
    /// </summary>
    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    /// <summary>The `October 2026` month label (docs/API.md §14).</summary>
    public static string MonthLabel(DateOnly date) =>
        string.Concat(MonthNames[date.Month - 1], " ", YearLabel(date));

    /// <summary>The `2026` year label (docs/API.md §14).</summary>
    public static string YearLabel(DateOnly date) =>
        date.Year.ToString(CultureInfo.InvariantCulture);

    /// <summary>The `2026-10` month key used by the history paging cursors.</summary>
    public static string MonthKey(DateOnly date) =>
        string.Concat(
            date.Year.ToString("D4", CultureInfo.InvariantCulture),
            "-",
            date.Month.ToString("D2", CultureInfo.InvariantCulture));
}
