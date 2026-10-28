using System.Text.Json;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Hardening for household settings payloads (issue #19)
/// duplicate-key semantics, UTF-16 length measurement, and the tzdata walk
/// fallback that must not poison the lazy timezone map.
/// </summary>
public sealed class HouseholdSettingsHardeningTests
{
    [Fact]
    public void Duplicate_json_keys_use_the_last_value_wins_semantics()
    {
        using var document = JsonDocument.Parse(
            """{"household_name":"First","household_name":"Second"}""");

        var validation = HouseholdSettingsPatchValidator.Validate(document.RootElement);

        validation.Patch.Should().NotBeNull();
        validation.Patch!.HouseholdName.Should().Be("Second");
    }

    [Fact]
    public void Household_name_length_is_measured_in_utf16_code_units()
    {
        // "😀" is two UTF-16 code units, so 60 emoji exceed the 100-unit cap.
        var tooLong = string.Concat(Enumerable.Repeat("😀", 60));
        tooLong.Length.Should().Be(120);

        using var rejected = JsonDocument.Parse(
            JsonSerializer.Serialize(new { household_name = tooLong }));
        var tooLongValidation = HouseholdSettingsPatchValidator.Validate(rejected.RootElement);
        tooLongValidation.Patch.Should().BeNull();
        tooLongValidation.FieldErrors.Should().ContainKey("household_name");

        // 49 emoji + "a" is 99 units, so it fits.
        var fitting = string.Concat(Enumerable.Repeat("😀", 49)) + "a";
        fitting.Length.Should().Be(99);

        using var accepted = JsonDocument.Parse(
            JsonSerializer.Serialize(new { household_name = fitting }));
        var fittingValidation = HouseholdSettingsPatchValidator.Validate(accepted.RootElement);
        fittingValidation.Patch.Should().NotBeNull();
        fittingValidation.Patch!.HouseholdName.Should().Be(fitting);
    }

    [Fact]
    public void Timezone_walk_failures_fall_back_to_the_system_zones()
    {
        var act = () => HouseholdSettingsPatchValidator.BuildCanonicalTimeZoneIds(
            _ => throw new IOException("simulated unreadable tzdata tree"));

        var canonical = act.Should().NotThrow().Subject;

        canonical.Should().NotBeEmpty();
        canonical.Should().ContainKey("UTC");
    }

    [Fact]
    public void Timezone_walk_failures_do_not_change_validation_behavior()
    {
        var canonical = HouseholdSettingsPatchValidator.BuildCanonicalTimeZoneIds(
            _ => throw new UnauthorizedAccessException("simulated permission failure"));

        // The produced map still resolves the canonical ids the validator uses.
        canonical.Values.Should().Contain("UTC");
    }
}
