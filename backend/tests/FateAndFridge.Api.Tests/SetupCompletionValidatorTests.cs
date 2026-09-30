using System.Text.Json;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Validation matrix for <c>POST /setup/complete</c> (docs/API.md §21): field
/// presence, household field reuse, admin iff multiuser, password policy, and
/// IP allowlist CIDR rules.
/// </summary>
public sealed class SetupCompletionValidatorTests
{
    [Fact]
    public void Valid_multiuser_request_is_accepted_and_normalized()
    {
        var body = ValidMultiuserBody();
        body["household_name"] = "  The Smiths  ";
        body["timezone"] = "america/chicago";

        var validation = Validate(body);

        validation.FieldErrors.Should().BeEmpty();
        validation.Request.Should().NotBeNull();
        var request = validation.Request!;
        request.HouseholdName.Should().Be("The Smiths");
        request.Timezone.Should().Be("America/Chicago");
        request.WeekStartsOn.Should().Be(0);
        request.DefaultDinerCount.Should().Be(4);
        request.StartupPage.Should().Be(StartupPage.Calendar);
        request.AuthMode.Should().Be(AuthMode.Multiuser);
        request.UpdateCheckEnabled.Should().BeTrue();
        request.Admin.Should().NotBeNull();
        request.Admin!.Username.Should().Be("smith");
        request.Admin.NormalizedUsername.Should().Be("smith");
        request.Admin.Password.Should().Be("correct horse battery staple");
        request.IpAllowlistEnabled.Should().BeFalse();
        request.IpAllowlistRanges.Should().BeEmpty();
    }

    [Fact]
    public void Valid_single_user_request_needs_no_admin()
    {
        var body = ValidMultiuserBody();
        body["auth_mode"] = "single_user";
        body.Remove("admin");

        var validation = Validate(body);

        validation.FieldErrors.Should().BeEmpty();
        validation.Request!.AuthMode.Should().Be(AuthMode.SingleUser);
        validation.Request.Admin.Should().BeNull();
    }

    [Fact]
    public void Bare_addresses_are_accepted_and_normalized_to_single_host_cidrs()
    {
        var body = ValidMultiuserBody();
        body["ip_allowlist"] = new
        {
            enabled = true,
            ranges = new[] { "10.0.0.1", "2001:db8::1" },
        };

        var validation = Validate(body);

        validation.FieldErrors.Should().BeEmpty();
        validation.Request!.IpAllowlistRanges.Should().Equal("10.0.0.1/32", "2001:db8::1/128");
    }

    [Fact]
    public void Explicit_cidr_ranges_are_stored_unchanged()
    {
        var body = ValidMultiuserBody();
        body["ip_allowlist"] = new { enabled = true, ranges = new[] { "10.0.0.0/8" } };

        var validation = Validate(body);

        validation.FieldErrors.Should().BeEmpty();
        validation.Request!.IpAllowlistRanges.Should().Equal("10.0.0.0/8");
    }

    [Fact]
    public void Valid_enabled_allowlist_keeps_ipv4_and_ipv6_ranges()
    {
        var body = ValidMultiuserBody();
        body["ip_allowlist"] = new
        {
            enabled = true,
            ranges = new[] { "192.168.0.0/24", "2001:db8::/32" },
        };

        var validation = Validate(body);

        validation.FieldErrors.Should().BeEmpty();
        validation.Request!.IpAllowlistEnabled.Should().BeTrue();
        validation.Request.IpAllowlistRanges.Should().Equal("192.168.0.0/24", "2001:db8::/32");
    }

    [Theory]
    [InlineData("household_name")]
    [InlineData("timezone")]
    [InlineData("week_starts_on")]
    [InlineData("default_diner_count")]
    [InlineData("startup_page")]
    [InlineData("auth_mode")]
    [InlineData("update_check_enabled")]
    public void Missing_required_fields_are_reported(string fieldName)
    {
        var body = ValidMultiuserBody();
        body.Remove(fieldName);

        var validation = Validate(body);

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey(fieldName);
    }

    [Fact]
    public void Non_object_body_is_rejected()
    {
        var validation = Validate("[]");

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey("body");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0001name")]
    public void Invalid_household_names_are_rejected(string householdName)
    {
        var body = ValidMultiuserBody();
        body["household_name"] = householdName;

        var validation = Validate(body);

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey("household_name");
    }

    [Fact]
    public void Overlong_household_name_is_rejected()
    {
        var body = ValidMultiuserBody();
        body["household_name"] = new string('a', HouseholdSettingsPatchValidator.MaxHouseholdNameLength + 1);

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("household_name");
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("")]
    public void Invalid_timezones_are_rejected(string timezone)
    {
        var body = ValidMultiuserBody();
        body["timezone"] = timezone;

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("timezone");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void Out_of_range_week_starts_on_is_rejected(int value)
    {
        var body = ValidMultiuserBody();
        body["week_starts_on"] = value;

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("week_starts_on");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Out_of_range_diner_count_is_rejected(int value)
    {
        var body = ValidMultiuserBody();
        body["default_diner_count"] = value;

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("default_diner_count");
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("Calendar")]
    public void Invalid_startup_pages_are_rejected(string page)
    {
        var body = ValidMultiuserBody();
        body["startup_page"] = page;

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("startup_page");
    }

    [Fact]
    public void Invalid_auth_modes_are_rejected()
    {
        var body = ValidMultiuserBody();
        body["auth_mode"] = "multi";

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("auth_mode");
    }

    [Fact]
    public void Multiuser_requires_an_admin_block()
    {
        var body = ValidMultiuserBody();
        body.Remove("admin");

        var validation = Validate(body);

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey("admin");
    }

    [Fact]
    public void Single_user_must_not_supply_an_admin_block()
    {
        var body = ValidMultiuserBody();
        body["auth_mode"] = "single_user";

        var validation = Validate(body);

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey("admin");
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("bad\u0001name")]
    public void Invalid_admin_usernames_are_rejected(string username)
    {
        var body = ValidMultiuserBody();
        body["admin"] = new { username, password = "correct horse battery staple" };

        var validation = Validate(body);

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey("admin.username");
    }

    [Fact]
    public void Overlong_admin_username_is_rejected()
    {
        var body = ValidMultiuserBody();
        body["admin"] = new
        {
            username = new string('a', SetupCompletionValidator.MaxUsernameLength + 1),
            password = "correct horse battery staple",
        };

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("admin.username");
    }

    [Theory]
    [InlineData(11)]
    [InlineData(129)]
    public void Admin_passwords_outside_the_policy_are_rejected(int length)
    {
        var body = ValidMultiuserBody();
        body["admin"] = new { username = "smith", password = new string('x', length) };

        var validation = Validate(body);

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey("admin.password");
    }

    [Fact]
    public void Non_boolean_update_check_flag_is_rejected()
    {
        var body = ValidMultiuserBody();
        body["update_check_enabled"] = "true";

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("update_check_enabled");
    }

    [Fact]
    public void Non_object_allowlist_is_rejected()
    {
        var body = ValidMultiuserBody();
        body["ip_allowlist"] = "192.168.0.0/24";

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("ip_allowlist");
    }

    [Fact]
    public void Enabled_allowlist_requires_at_least_one_range()
    {
        var body = ValidMultiuserBody();
        body["ip_allowlist"] = new { enabled = true, ranges = Array.Empty<string>() };

        var validation = Validate(body);

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey("ip_allowlist.ranges");
    }

    [Theory]
    [InlineData("999.1.1.0/24")]
    [InlineData("not-a-range")]
    [InlineData("")]
    [InlineData("10.0.0.1/33")]
    [InlineData("10.0.0.1/")]
    public void Invalid_allowlist_ranges_are_rejected(string range)
    {
        var body = ValidMultiuserBody();
        body["ip_allowlist"] = new { enabled = true, ranges = new[] { range } };

        var validation = Validate(body);

        validation.Request.Should().BeNull();
        validation.FieldErrors.Should().ContainKey("ip_allowlist.ranges");
    }

    [Fact]
    public void Non_array_allowlist_ranges_are_rejected()
    {
        var body = ValidMultiuserBody();
        body["ip_allowlist"] = new { enabled = false, ranges = "192.168.0.0/24" };

        var validation = Validate(body);

        validation.FieldErrors.Should().ContainKey("ip_allowlist.ranges");
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var body = ValidMultiuserBody();
        body["unexpected"] = new { nested = true };

        var validation = Validate(body);

        validation.FieldErrors.Should().BeEmpty();
        validation.Request.Should().NotBeNull();
    }

    private static Dictionary<string, object?> ValidMultiuserBody() => new(StringComparer.Ordinal)
    {
        ["household_name"] = "The Smiths",
        ["timezone"] = "America/Chicago",
        ["week_starts_on"] = 0,
        ["default_diner_count"] = 4,
        ["startup_page"] = "calendar",
        ["auth_mode"] = "multiuser",
        ["admin"] = new { username = "smith", password = "correct horse battery staple" },
        ["ip_allowlist"] = new { enabled = false, ranges = Array.Empty<string>() },
        ["update_check_enabled"] = true,
    };

    private static SetupCompletionValidation Validate(IReadOnlyDictionary<string, object?> body) =>
        Validate(JsonSerializer.Serialize(body));

    private static SetupCompletionValidation Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return SetupCompletionValidator.Validate(document.RootElement);
    }
}
