using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FateAndFridge.Api.Migrations
{
    /// <inheritdoc />
    public partial class M6Randomizer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "randomizer_favorite_multiplier",
                table: "household_settings",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 1.10m);

            migrationBuilder.AddColumn<decimal>(
                name: "randomizer_protein_multiplier",
                table: "household_settings",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 0.70m);

            migrationBuilder.AddColumn<int>(
                name: "randomizer_protein_neighbor_distance",
                table: "household_settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "randomizer_repeat_lookahead_days",
                table: "household_settings",
                type: "integer",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<int>(
                name: "randomizer_repeat_lookback_days",
                table: "household_settings",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<decimal>(
                name: "randomizer_repeat_multiplier",
                table: "household_settings",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 0.10m);

            migrationBuilder.AddColumn<int>(
                name: "randomizer_spacing_far_days",
                table: "household_settings",
                type: "integer",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<int>(
                name: "randomizer_spacing_near_days",
                table: "household_settings",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "randomizer_spacing_search_days",
                table: "household_settings",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<decimal>(
                name: "randomizer_special_day_multiplier",
                table: "household_settings",
                type: "numeric(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 0.02m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "randomizer_favorite_multiplier",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_protein_multiplier",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_protein_neighbor_distance",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_repeat_lookahead_days",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_repeat_lookback_days",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_repeat_multiplier",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_spacing_far_days",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_spacing_near_days",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_spacing_search_days",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "randomizer_special_day_multiplier",
                table: "household_settings");
        }
    }
}
