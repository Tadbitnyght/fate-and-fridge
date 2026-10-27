using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FateAndFridge.Api.Migrations
{
    /// <inheritdoc />
    public partial class M13Backups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_metadata",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    filename = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    app_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    schema_version = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_metadata", x => x.id);
                    table.ForeignKey(
                        name: "fk_backup_metadata_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_metadata_created_at",
                table: "backup_metadata",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_backup_metadata_created_by_user_id",
                table: "backup_metadata",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_backup_metadata_filename",
                table: "backup_metadata",
                column: "filename",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_metadata");
        }
    }
}
