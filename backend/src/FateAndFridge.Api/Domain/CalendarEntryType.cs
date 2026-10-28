namespace FateAndFridge.Api.Domain;

/// <summary>
/// The meal/special-event type of a calendar entry (docs/DATABASE.md §8
/// docs/PRODUCT_SPEC.md §7). Stored as the stable snake_case text below.
/// </summary>
public enum CalendarEntryType
{
    /// <summary>A planned recipe.</summary>
    Recipe,

    /// <summary>Eating out; no groceries.</summary>
    EatOut,

    /// <summary>Fend for yourself; no groceries.</summary>
    FendForYourself,

    /// <summary>Hosting family dinner; optional recipe and diner count.</summary>
    HostFamilyDinner,

    /// <summary>Going to family dinner; optional host/location.</summary>
    GoToFamilyDinner,

    /// <summary>A holiday; optional custom name and recipe.</summary>
    Holiday,

    /// <summary>A miscellaneous special day; custom label required.</summary>
    Misc,

    /// <summary>Vacation; blocks automated meal generation.</summary>
    Vacation,
}
