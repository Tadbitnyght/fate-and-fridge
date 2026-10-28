namespace FateAndFridge.Api.Domain;

/// <summary>
/// Key/value application setting. Structured values are stored as JSONB
/// <see cref="UpdatedAt"/> is set by the application and is always UTC.
/// </summary>
public sealed class ApplicationSetting
{
    public required string Key { get; set; }

    /// <summary>JSON payload, or <see langword="null"/> when the setting is absent or unset.</summary>
    public string? ValueJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
