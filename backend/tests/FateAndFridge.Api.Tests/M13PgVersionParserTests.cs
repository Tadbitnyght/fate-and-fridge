using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Unit checks for the M13-01 pg_dump/server version parsing and the
/// client-vs-server major comparison (docs/M13_PLAN.md §1.3). No database and no
/// process is involved.
/// </summary>
public sealed class M13PgVersionParserTests
{
    [Theory]
    [InlineData("pg_dump (PostgreSQL) 17.4 (Debian 17.4-1.pgdg120+1)", 17)]
    [InlineData("pg_dump (PostgreSQL) 17.0\n", 17)]
    [InlineData("pg_dump (PostgreSQL) 16.10 (Ubuntu 16.10-1)", 16)]
    [InlineData("pg_dump (PostgreSQL) 18.0", 18)]
    [InlineData("pg_dump (PostgreSQL) 9.6.24", 9)]
    public void Client_version_output_yields_the_major(string output, int expectedMajor)
    {
        PgVersionParser.TryParseClientMajor(output, out var major).Should().BeTrue();

        major.Should().Be(expectedMajor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pg_dump: error: could not connect")]
    [InlineData("PostgreSQL 17.4")]
    [InlineData("pg_dump (PostgreSQL)")]
    [InlineData("pg_dump (PostgreSQL) latest")]
    public void Unparseable_client_output_is_rejected(string? output)
    {
        PgVersionParser.TryParseClientMajor(output, out var major).Should().BeFalse();

        major.Should().Be(0);
    }

    [Theory]
    [InlineData("170004", 17)]
    [InlineData("170000", 17)]
    [InlineData("90624", 9)]
    public void Server_version_number_yields_the_major(string versionNumber, int expectedMajor)
    {
        PgVersionParser.TryParseServerVersionNumber(versionNumber, out var major).Should().BeTrue();

        major.Should().Be(expectedMajor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-170004")]
    [InlineData("17.4")]
    public void Invalid_server_version_number_is_rejected(string? versionNumber)
    {
        PgVersionParser.TryParseServerVersionNumber(versionNumber, out var major).Should().BeFalse();

        major.Should().Be(0);
    }

    [Theory]
    [InlineData("17.4 (Debian 17.4-1.pgdg120+1)", 17)]
    [InlineData("16.3", 16)]
    public void Server_version_text_yields_the_major(string version, int expectedMajor)
    {
        PgVersionParser.TryParseServerVersion(version, out var major).Should().BeTrue();

        major.Should().Be(expectedMajor);
    }

    [Theory]
    [InlineData(17, 17, true)]
    [InlineData(18, 17, true)]
    [InlineData(16, 17, false)]
    [InlineData(17, 18, false)]
    [InlineData(null, 17, false)]
    [InlineData(17, null, false)]
    [InlineData(null, null, false)]
    public void Client_must_be_at_least_as_new_as_the_server(int? clientMajor, int? serverMajor, bool expected)
    {
        var status = new PgToolingStatus(
            clientMajor is null ? null : $"pg_dump (PostgreSQL) {clientMajor}.0",
            clientMajor,
            serverMajor is null ? null : $"{serverMajor}.0",
            serverMajor);

        status.SupportsBackup.Should().Be(expected);
    }
}
