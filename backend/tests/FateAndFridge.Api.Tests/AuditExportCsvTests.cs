using System.Globalization;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure unit tests for the export CSV projection: the fixed header, RFC 4180 quoting for commas/quotes/
/// newlines, and spreadsheet formula-injection neutralisation. No database or
/// HTTP is involved.
/// </summary>
public sealed class AuditExportCsvTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);

    private static AuditLogItemRecord Item(
        string? entityId = "7",
        string? reason = "planned",
        string? beforeJson = """{"entry_type":"eat_out"}""",
        string? afterJson = """{"entry_type":"recipe"}""",
        string? metadataJson = """{"meal_date":"2026-11-15"}""") =>
        new(
            Id: 42,
            Source: AuditLogSources.Audit,
            RecordedAt: RecordedAt,
            ActorUserId: 1,
            ActorUsername: "root",
            EventType: AuditEventTypes.CalendarUpdated,
            EntityType: AuditEntityTypes.CalendarEntry,
            EntityId: entityId,
            Summary: "Calendar entry changed",
            Reason: reason,
            IpAddress: "203.0.113.11",
            BeforeJson: beforeJson,
            AfterJson: afterJson,
            MetadataJson: metadataJson);

    [Fact]
    public void The_header_is_fixed_and_in_the_documented_order()
    {
        var csv = AuditLogCsv.Write([Item()]);
        var lines = csv.Split("\r\n");

        lines[0].Should().Be(
            "id,source,recorded_at,actor_user_id,actor_username,event_type,entity_type," +
            "entity_id,summary,reason,ip_address,before,after,metadata");
        AuditLogQueryService.CsvColumns.Should().Equal(
            "id", "source", "recorded_at", "actor_user_id", "actor_username", "event_type",
            "entity_type", "entity_id", "summary", "reason", "ip_address", "before", "after",
            "metadata");
    }

    [Fact]
    public void An_empty_view_still_writes_the_header_only()
    {
        var csv = AuditLogCsv.Write([]);

        csv.Should().Be(
            "id,source,recorded_at,actor_user_id,actor_username,event_type,entity_type," +
            "entity_id,summary,reason,ip_address,before,after,metadata\r\n");
    }

    [Fact]
    public void A_plain_row_has_all_fourteen_columns_and_no_trailing_newline()
    {
        var csv = AuditLogCsv.Write([Item()]);

        csv.Should().NotEndWith("\r\n");
        var rows = csv.Split("\r\n");
        rows.Should().HaveCount(2);
        rows[1].Should().StartWith(
            "42,audit,2026-11-15T18:00:00.0000000+00:00,1,root,calendar.updated," +
            "calendar_entry,7,Calendar entry changed,planned,203.0.113.11,");
        // The stored JSON documents contain quotes, so each is quoted and the
        // embedded quotes are doubled.
        rows[1].Should().EndWith(
            "\"{\"\"entry_type\"\":\"\"eat_out\"\"}\"," +
            "\"{\"\"entry_type\"\":\"\"recipe\"\"}\"," +
            "\"{\"\"meal_date\"\":\"\"2026-11-15\"\"}\"");
    }

    [Fact]
    public void A_null_actor_is_an_empty_cell()
    {
        var item = Item() with { ActorUserId = null, ActorUsername = null };
        var row = AuditLogCsv.Write([item]).Split("\r\n")[1];

        row.Should().StartWith("42,audit,2026-11-15T18:00:00.0000000+00:00,,,calendar.updated,");
    }

    [Fact]
    public void Commas_quotes_and_newlines_are_quoted_and_escaped()
    {
        var item = Item(
            reason: "first line,\n\"quoted\" second line",
            metadataJson: "{}");

        var row = AuditLogCsv.Write([item]).Split("\r\n")[1];

        row.Should().Contain("\"first line,\n\"\"quoted\"\" second line\"");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tpayload")]
    [InlineData("\rpayload")]
    public void A_formula_like_value_is_neutralised_with_a_leading_apostrophe(string dangerous)
    {
        AuditLogCsv.NeutraliseFormula(dangerous).Should().Be($"'{dangerous}");
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("42")]
    [InlineData("2026-11-15T18:00:00.0000000+00:00")]
    [InlineData("")]
    [InlineData("\"already quoted\"")]
    public void Other_values_are_left_alone_apart_from_quoting(string safe)
    {
        var expected = safe == "\"already quoted\"" ? "\"\"\"already quoted\"\"\"" : safe;
        AuditLogCsv.FormatCell(safe).Should().Be(expected);
    }

    [Fact]
    public void Neutralisation_happens_before_quoting_so_the_apostrophe_is_inside_the_cell()
    {
        AuditLogCsv.FormatCell("=1,2").Should().Be("\"'=1,2\"");
    }

    [Fact]
    public void A_dangerous_cell_round_trips_through_a_row()
    {
        var item = Item(entityId: "=cmd|' /C calc'!A0", reason: "@import");

        var row = AuditLogCsv.Write([item]).Split("\r\n")[1];

        row.Should().Contain("'=cmd|' /C calc'!A0");
        row.Should().Contain("'@import");
    }

    [Fact]
    public void The_documented_column_count_matches_every_written_row()
    {
        var items = new[]
        {
            Item(),
            Item(entityId: null, reason: null, beforeJson: null, afterJson: null, metadataJson: null),
        };

        var rows = AuditLogCsv.Write(items).Split("\r\n");

        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(row =>
            row.Split(',').Length == AuditLogQueryService.CsvColumns.Length,
            "no row may drop or add a column");
        rows[2].Should().EndWith(",Calendar entry changed,,203.0.113.11,,,");
    }
}
