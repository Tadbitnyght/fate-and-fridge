using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FateAndFridge.Api.Migrations
{
    /// <inheritdoc />
    public partial class M6OperationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "metadata_json",
                table: "calendar_entry_history",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "metadata_json",
                table: "calendar_entry_history");
        }
    }
}
