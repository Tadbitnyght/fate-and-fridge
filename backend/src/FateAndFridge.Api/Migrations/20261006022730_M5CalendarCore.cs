using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FateAndFridge.Api.Migrations
{
    /// <inheritdoc />
    public partial class M5CalendarCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "historical_boundary_date",
                table: "household_settings",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    meal_date = table.Column<DateOnly>(type: "date", nullable: false),
                    entry_type = table.Column<string>(type: "text", nullable: false),
                    recipe_id = table.Column<long>(type: "bigint", nullable: true),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    diner_count = table.Column<int>(type: "integer", nullable: true),
                    restaurant_name = table.Column<string>(type: "text", nullable: true),
                    location_text = table.Column<string>(type: "text", nullable: true),
                    custom_label = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    icon_key = table.Column<string>(type: "text", nullable: true),
                    include_groceries = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    snapshot_recipe_name = table.Column<string>(type: "text", nullable: true),
                    snapshot_display_text = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    updated_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_calendar_entries_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_calendar_entries_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calendar_entries_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "calendar_entry_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    calendar_entry_id = table.Column<long>(type: "bigint", nullable: true),
                    meal_date = table.Column<DateOnly>(type: "date", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    changed_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    change_reason = table.Column<string>(type: "text", nullable: true),
                    is_historical_correction = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_entry_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_calendar_entry_history_calendar_entries_calendar_entry_id",
                        column: x => x.calendar_entry_id,
                        principalTable: "calendar_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_calendar_entry_history_users_changed_by_user_id",
                        column: x => x.changed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entries_created_by_user_id",
                table: "calendar_entries",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entries_meal_date",
                table: "calendar_entries",
                column: "meal_date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entries_recipe_id",
                table: "calendar_entries",
                column: "recipe_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entries_updated_by_user_id",
                table: "calendar_entries",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entry_history_calendar_entry_id",
                table: "calendar_entry_history",
                column: "calendar_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entry_history_changed_at",
                table: "calendar_entry_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entry_history_changed_by_user_id",
                table: "calendar_entry_history",
                column: "changed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entry_history_operation_id",
                table: "calendar_entry_history",
                column: "operation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_entry_history");

            migrationBuilder.DropTable(
                name: "calendar_entries");

            migrationBuilder.DropColumn(
                name: "historical_boundary_date",
                table: "household_settings");
        }
    }
}
