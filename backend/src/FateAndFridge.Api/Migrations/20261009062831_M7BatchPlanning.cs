using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FateAndFridge.Api.Migrations
{
    /// <inheritdoc />
    public partial class M7BatchPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "batch_operation_id",
                table: "calendar_entry_history",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_reroll_drafts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    token = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    proposal_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_reroll_drafts", x => x.id);
                    table.ForeignKey(
                        name: "fk_calendar_reroll_drafts_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_entry_history_batch_operation_id",
                table: "calendar_entry_history",
                column: "batch_operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_reroll_drafts_actor_user_id",
                table: "calendar_reroll_drafts",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_reroll_drafts_expires_at",
                table: "calendar_reroll_drafts",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_reroll_drafts_token",
                table: "calendar_reroll_drafts",
                column: "token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_reroll_drafts");

            migrationBuilder.DropIndex(
                name: "ix_calendar_entry_history_batch_operation_id",
                table: "calendar_entry_history");

            migrationBuilder.DropColumn(
                name: "batch_operation_id",
                table: "calendar_entry_history");
        }
    }
}
