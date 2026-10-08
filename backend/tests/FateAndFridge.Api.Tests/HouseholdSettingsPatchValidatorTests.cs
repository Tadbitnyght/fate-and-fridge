using System.Text.Json;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Field-level validation matrix for the household settings PATCH body
/// (docs/API.md §21). Pure JSON; no database is involved.
/// </summary>
public sealed class HouseholdSettingsPatchValidatorTests
{
    [Fact]
    public void Full_valid_patch_is_accepted_with_a_trimmed_name()
    {
        var result = Validate(
            """
            {
              "household_name": "  The Smiths  ",
              "timezone": "America/Chicago",
              "week_starts_on": 6,
              "default_diner_count": 20,
              "startup_page": "dashboard"
            }
            """);

        result.FieldErrors.Should().BeEmpty();
        result.Patch.Should().Be(new HouseholdSettingsPatch(
            "The Smiths",
            "America/Chicago",
            6,
            20,
            StartupPage.Dashboard));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/Chicago")]
    [InlineData("Europe/Berlin")]
    [InlineData("Asia/Tokyo")]
    public void Valid_iana_timezones_are_accepted(string timezone)
    {
        var result = Validate($$"""{"timezone": "{{timezone}}"}""");

        result.FieldErrors.Should().BeEmpty();
        result.Patch!.Timezone.Should().Be(timezone);
    }

    [Theory]
    [InlineData("america/chicago", "America/Chicago")]
    [InlineData("AMERICA/CHICAGO", "America/Chicago")]
    [InlineData("America/Chicago", "America/Chicago")]
    [InlineData("utc", "UTC")]
    [InlineData("UTC", "UTC")]
    [InlineData("Etc/GMT+5", "Etc/GMT+5")]
    [InlineData("etc/gmt+5", "Etc/GMT+5")]
    public void Timezones_are_resolved_to_canonical_casing(string input, string expected)
    {
        var result = Validate(JsonSerializer.Serialize(new { timezone = input }));

        result.FieldErrors.Should().BeEmpty();
        result.Patch!.Timezone.Should().Be(expected);
    }

    [Theory]
    [InlineData("localTime")]
    [InlineData("posixrules")]
    [InlineData("Factory")]
    public void Pseudo_timezone_files_are_not_iana_ids_and_are_rejected(string timezone)
    {
        var result = Validate(JsonSerializer.Serialize(new { timezone }));

        result.Patch.Should().BeNull();
        result.FieldErrors.Should().ContainKey("timezone");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Week_starts_on_boundaries_are_accepted(int weekStartsOn)
    {
        var result = Validate($$"""{"week_starts_on": {{weekStartsOn}}}""");

        result.FieldErrors.Should().BeEmpty();
        result.Patch!.WeekStartsOn.Should().Be(weekStartsOn);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void Diner_count_boundaries_are_accepted(int dinerCount)
    {
        var result = Validate($$"""{"default_diner_count": {{dinerCount}}}""");

        result.FieldErrors.Should().BeEmpty();
        result.Patch!.DefaultDinerCount.Should().Be(dinerCount);
    }

    [Fact]
    public void Randomizer_patch_accepts_every_field_at_its_minimum()
    {
        var result = Validate(
            """
            {
              "randomizer_repeat_lookback_days": 0,
              "randomizer_repeat_lookahead_days": 0,
              "randomizer_repeat_multiplier": 0.01,
              "randomizer_special_day_multiplier": 0.00,
              "randomizer_protein_multiplier": 0.10,
              "randomizer_protein_neighbor_distance": 1,
              "randomizer_favorite_multiplier": 1.00,
              "randomizer_spacing_near_days": 1,
              "randomizer_spacing_far_days": 1,
              "randomizer_spacing_search_days": 7
            }
            """);

        result.FieldErrors.Should().BeEmpty();
        result.Patch.Should().Be(new HouseholdSettingsPatch(
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0.01m,
            0.00m,
            0.10m,
            1,
            1.00m,
            1,
            1,
            7));
    }

    [Fact]
    public void Randomizer_patch_accepts_every_field_at_its_maximum()
    {
        var result = Validate(
            """
            {
              "randomizer_repeat_lookback_days": 60,
              "randomizer_repeat_lookahead_days": 60,
              "randomizer_repeat_multiplier": 1.00,
              "randomizer_special_day_multiplier": 1.00,
              "randomizer_protein_multiplier": 1.00,
              "randomizer_protein_neighbor_distance": 7,
              "randomizer_favorite_multiplier": 2.00,
              "randomizer_spacing_near_days": 30,
              "randomizer_spacing_far_days": 60,
              "randomizer_spacing_search_days": 365
            }
            """);

        result.FieldErrors.Should().BeEmpty();
        result.Patch.Should().Be(new HouseholdSettingsPatch(
            null,
            null,
            null,
            null,
            null,
            60,
            60,
            1.00m,
            1.00m,
            1.00m,
            7,
            2.00m,
            30,
            60,
            365));
    }

    [Fact]
    public void Randomizer_only_patch_is_not_treated_as_empty()
    {
        var result = Validate("""{"randomizer_repeat_lookback_days": 7}""");

        result.FieldErrors.Should().BeEmpty();
        result.Patch!.RandomizerRepeatLookbackDays.Should().Be(7);
    }

    [Fact]
    public void Spacing_far_below_near_in_the_same_patch_is_rejected()
    {
        var patch = new HouseholdSettingsPatch(
            null,
            null,
            null,
            null,
            null,
            RandomizerSpacingNearDays: 10,
            RandomizerSpacingFarDays: 5);

        var errors = HouseholdSettingsPatchValidator.ValidateAgainstCurrent(patch, CurrentSettings());

        errors.Should().ContainKey("randomizer_spacing_far_days");
        errors["randomizer_spacing_far_days"].Should().ContainSingle();
    }

    [Fact]
    public void Spacing_far_equal_to_near_is_accepted()
    {
        var patch = new HouseholdSettingsPatch(
            null,
            null,
            null,
            null,
            null,
            RandomizerSpacingNearDays: 8,
            RandomizerSpacingFarDays: 8);

        HouseholdSettingsPatchValidator.ValidateAgainstCurrent(patch, CurrentSettings())
            .Should().BeEmpty();
    }

    [Fact]
    public void Spacing_cross_field_check_uses_the_current_row_for_omitted_fields()
    {
        // Raising near above the stored far violates the coupling.
        var nearOnly = new HouseholdSettingsPatch(
            null,
            null,
            null,
            null,
            null,
            RandomizerSpacingNearDays: 12);
        HouseholdSettingsPatchValidator.ValidateAgainstCurrent(nearOnly, CurrentSettings(near: 3, far: 8))
            .Should().ContainKey("randomizer_spacing_far_days");

        // Lowering far below the stored near violates the coupling.
        var farOnly = new HouseholdSettingsPatch(
            null,
            null,
            null,
            null,
            null,
            RandomizerSpacingFarDays: 2);
        HouseholdSettingsPatchValidator.ValidateAgainstCurrent(farOnly, CurrentSettings(near: 3, far: 8))
            .Should().ContainKey("randomizer_spacing_far_days");

        // A change that keeps far >= near passes.
        var compatible = new HouseholdSettingsPatch(
            null,
            null,
            null,
            null,
            null,
            RandomizerSpacingFarDays: 20);
        HouseholdSettingsPatchValidator.ValidateAgainstCurrent(compatible, CurrentSettings(near: 3, far: 8))
            .Should().BeEmpty();
    }

    [Fact]
    public void Household_name_lengths_one_and_one_hundred_are_accepted()
    {
        foreach (var name in new[] { "x", new string('x', 100) })
        {
            var result = Validate($$"""{"household_name": "{{name}}"}""");

            result.FieldErrors.Should().BeEmpty();
            result.Patch!.HouseholdName.Should().Be(name);
        }
    }

    [Fact]
    public void Household_name_longer_than_one_hundred_characters_is_rejected()
    {
        var result = Validate($$"""{"household_name": "{{new string('x', 101)}}"}""");

        result.Patch.Should().BeNull();
        result.FieldErrors.Should().ContainKey("household_name");
    }

    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u0001")]
    [InlineData("\u001F")]
    [InlineData("\u007F")]
    [InlineData("bad\u0000name")]
    [InlineData("line\u000Abreak")]
    [InlineData("tab\there")]
    public void Household_names_with_control_characters_are_rejected(string name)
    {
        var result = Validate(JsonSerializer.Serialize(new { household_name = name }));

        result.Patch.Should().BeNull();
        result.FieldErrors.Should().ContainKey("household_name");
        result.FieldErrors["household_name"].Should().ContainSingle()
            .Which.Should().Contain("control characters");
    }

    [Theory]
    [InlineData("Smith Family")]
    [InlineData("Café Zoë")]
    [InlineData("Smiths — Kitchen")]
    [InlineData("日本語の家庭")]
    [InlineData("O'Brien Household")]
    public void Household_names_with_ordinary_unicode_and_spaces_are_accepted(string name)
    {
        var result = Validate(JsonSerializer.Serialize(new { household_name = name }));

        result.FieldErrors.Should().BeEmpty();
        result.Patch!.HouseholdName.Should().Be(name);
    }

    [Theory]
    [InlineData("""{"household_name": ""}""", "household_name")]
    [InlineData("""{"household_name": "   "}""", "household_name")]
    [InlineData("""{"household_name": null}""", "household_name")]
    [InlineData("""{"household_name": 42}""", "household_name")]
    [InlineData("""{"timezone": ""}""", "timezone")]
    [InlineData("""{"timezone": "   "}""", "timezone")]
    [InlineData("""{"timezone": "Mars/Olympus"}""", "timezone")]
    [InlineData("""{"timezone": "../../etc/passwd"}""", "timezone")]
    [InlineData("""{"timezone": "/etc/passwd"}""", "timezone")]
    [InlineData("""{"timezone": "America/../America/Chicago"}""", "timezone")]
    [InlineData("""{"timezone": "right/America/Chicago"}""", "timezone")]
    [InlineData("""{"timezone": 7}""", "timezone")]
    [InlineData("""{"week_starts_on": -1}""", "week_starts_on")]
    [InlineData("""{"week_starts_on": 7}""", "week_starts_on")]
    [InlineData("""{"week_starts_on": 3.5}""", "week_starts_on")]
    [InlineData("""{"week_starts_on": "3"}""", "week_starts_on")]
    [InlineData("""{"week_starts_on": null}""", "week_starts_on")]
    [InlineData("""{"week_starts_on": 2147483648}""", "week_starts_on")]
    [InlineData("""{"default_diner_count": 0}""", "default_diner_count")]
    [InlineData("""{"default_diner_count": 21}""", "default_diner_count")]
    [InlineData("""{"default_diner_count": "4"}""", "default_diner_count")]
    [InlineData("""{"default_diner_count": null}""", "default_diner_count")]
    [InlineData("""{"startup_page": "Calendar"}""", "startup_page")]
    [InlineData("""{"startup_page": "CALENDAR"}""", "startup_page")]
    [InlineData("""{"startup_page": "dashboard "}""", "startup_page")]
    [InlineData("""{"startup_page": "home"}""", "startup_page")]
    [InlineData("""{"randomizer_repeat_lookback_days": -1}""", "randomizer_repeat_lookback_days")]
    [InlineData("""{"randomizer_repeat_lookback_days": 61}""", "randomizer_repeat_lookback_days")]
    [InlineData("""{"randomizer_repeat_lookahead_days": -1}""", "randomizer_repeat_lookahead_days")]
    [InlineData("""{"randomizer_repeat_lookahead_days": 61}""", "randomizer_repeat_lookahead_days")]
    [InlineData("""{"randomizer_repeat_multiplier": 0.00}""", "randomizer_repeat_multiplier")]
    [InlineData("""{"randomizer_repeat_multiplier": 1.01}""", "randomizer_repeat_multiplier")]
    [InlineData("""{"randomizer_repeat_multiplier": "0.10"}""", "randomizer_repeat_multiplier")]
    [InlineData("""{"randomizer_repeat_multiplier": 1e400}""", "randomizer_repeat_multiplier")]
    [InlineData("""{"randomizer_special_day_multiplier": -0.01}""", "randomizer_special_day_multiplier")]
    [InlineData("""{"randomizer_special_day_multiplier": 1.01}""", "randomizer_special_day_multiplier")]
    [InlineData("""{"randomizer_special_day_multiplier": null}""", "randomizer_special_day_multiplier")]
    [InlineData("""{"randomizer_protein_multiplier": 0.09}""", "randomizer_protein_multiplier")]
    [InlineData("""{"randomizer_protein_multiplier": 1.01}""", "randomizer_protein_multiplier")]
    [InlineData("""{"randomizer_protein_neighbor_distance": 0}""", "randomizer_protein_neighbor_distance")]
    [InlineData("""{"randomizer_protein_neighbor_distance": 8}""", "randomizer_protein_neighbor_distance")]
    [InlineData("""{"randomizer_favorite_multiplier": 0.99}""", "randomizer_favorite_multiplier")]
    [InlineData("""{"randomizer_favorite_multiplier": 2.01}""", "randomizer_favorite_multiplier")]
    [InlineData("""{"randomizer_spacing_near_days": 0}""", "randomizer_spacing_near_days")]
    [InlineData("""{"randomizer_spacing_near_days": 31}""", "randomizer_spacing_near_days")]
    [InlineData("""{"randomizer_spacing_far_days": 0}""", "randomizer_spacing_far_days")]
    [InlineData("""{"randomizer_spacing_far_days": 61}""", "randomizer_spacing_far_days")]
    [InlineData("""{"randomizer_spacing_search_days": 6}""", "randomizer_spacing_search_days")]
    [InlineData("""{"randomizer_spacing_search_days": 366}""", "randomizer_spacing_search_days")]
    [InlineData("""{"randomizer_spacing_search_days": "90"}""", "randomizer_spacing_search_days")]
    [InlineData("""{"randomizer_spacing_search_days": null}""", "randomizer_spacing_search_days")]
    [InlineData("""{"startup_page": null}""", "startup_page")]
    public void Invalid_field_values_are_rejected_with_a_single_field_error(string json, string expectedField)
    {
        var result = Validate(json);

        result.Patch.Should().BeNull();
        result.FieldErrors.Should().ContainKey(expectedField);
        result.FieldErrors[expectedField].Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Multiple_invalid_fields_are_all_reported()
    {
        var result = Validate("""{"timezone": "Mars/Olympus", "startup_page": "none", "week_starts_on": 9}""");

        result.Patch.Should().BeNull();
        result.FieldErrors.Keys.Should().BeEquivalentTo(["timezone", "startup_page", "week_starts_on"]);
    }

    [Fact]
    public void Unknown_fields_are_ignored_when_a_known_field_is_present()
    {
        var result = Validate(
            """{"household_name": "Renamed", "auth_mode": "multiuser", "unknown": {"nested": true}}""");

        result.FieldErrors.Should().BeEmpty();
        result.Patch.Should().Be(new HouseholdSettingsPatch("Renamed", null, null, null, null));
    }

    [Fact]
    public void Duplicate_known_keys_follow_last_one_wins()
    {
        var result = Validate("""{"household_name": "First", "household_name": "Second"}""");

        result.FieldErrors.Should().BeEmpty();
        result.Patch!.HouseholdName.Should().Be("Second");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"auth_mode": "multiuser"}""")]
    [InlineData("""{"unknown": 1, "other": "x"}""")]
    public void Patch_without_known_fields_is_rejected_as_empty(string json)
    {
        var result = Validate(json);

        result.Patch.Should().BeNull();
        result.FieldErrors.Should().ContainKey("body");
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Non_object_bodies_are_rejected(string json)
    {
        var result = Validate(json);

        result.Patch.Should().BeNull();
        result.FieldErrors.Should().ContainKey("body");
    }

    private static HouseholdSettings CurrentSettings(int near = 3, int far = 8) => new()
    {
        Id = 1,
        HouseholdName = "Test Household",
        Timezone = "America/Chicago",
        RandomizerSpacingNearDays = near,
        RandomizerSpacingFarDays = far,
    };

    private static HouseholdSettingsPatchValidation Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return HouseholdSettingsPatchValidator.Validate(document.RootElement);
    }
}
