using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FateAndFridge.Api.Migrations
{
    /// <inheritdoc />
    public partial class M8Groceries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "grocery_check_state",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_identity_hash = table.Column<string>(type: "text", nullable: false),
                    @checked = table.Column<bool>(name: "checked", type: "boolean", nullable: false, defaultValue: false),
                    checked_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grocery_check_state", x => x.id);
                    table.ForeignKey(
                        name: "fk_grocery_check_state_users_checked_by_user_id",
                        column: x => x.checked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "grocery_staples",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    ingredient_id = table.Column<long>(type: "bigint", nullable: true),
                    department_id = table.Column<long>(type: "bigint", nullable: true),
                    default_quantity_numeric = table.Column<decimal>(type: "numeric(28,14)", precision: 28, scale: 14, nullable: true),
                    unit_id = table.Column<long>(type: "bigint", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grocery_staples", x => x.id);
                    table.ForeignKey(
                        name: "fk_grocery_staples_grocery_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "grocery_departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grocery_staples_ingredients_ingredient_id",
                        column: x => x.ingredient_id,
                        principalTable: "ingredients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grocery_staples_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "manual_grocery_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    ingredient_id = table.Column<long>(type: "bigint", nullable: true),
                    department_id = table.Column<long>(type: "bigint", nullable: true),
                    quantity_numeric = table.Column<decimal>(type: "numeric(28,14)", precision: 28, scale: 14, nullable: true),
                    unit_id = table.Column<long>(type: "bigint", nullable: true),
                    range_start = table.Column<DateOnly>(type: "date", nullable: false),
                    range_days = table.Column<int>(type: "integer", nullable: false),
                    save_for_future = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_grocery_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_manual_grocery_items_grocery_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "grocery_departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_manual_grocery_items_ingredients_ingredient_id",
                        column: x => x.ingredient_id,
                        principalTable: "ingredients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_manual_grocery_items_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_manual_grocery_items_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_grocery_preferences",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    hide_checked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    range_preset = table.Column<string>(type: "text", nullable: true),
                    range_start = table.Column<DateOnly>(type: "date", nullable: true),
                    range_days = table.Column<int>(type: "integer", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_grocery_preferences", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_user_grocery_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_grocery_check_state_checked_by_user_id",
                table: "grocery_check_state",
                column: "checked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_grocery_check_state_item_identity_hash",
                table: "grocery_check_state",
                column: "item_identity_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_grocery_staples_department_id",
                table: "grocery_staples",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_grocery_staples_ingredient_id",
                table: "grocery_staples",
                column: "ingredient_id");

            migrationBuilder.CreateIndex(
                name: "ix_grocery_staples_unit_id",
                table: "grocery_staples",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_grocery_items_created_by_user_id",
                table: "manual_grocery_items",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_grocery_items_department_id",
                table: "manual_grocery_items",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_grocery_items_ingredient_id",
                table: "manual_grocery_items",
                column: "ingredient_id");

            migrationBuilder.CreateIndex(
                name: "ix_manual_grocery_items_range_start_range_days",
                table: "manual_grocery_items",
                columns: new[] { "range_start", "range_days" });

            migrationBuilder.CreateIndex(
                name: "ix_manual_grocery_items_unit_id",
                table: "manual_grocery_items",
                column: "unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "grocery_check_state");

            migrationBuilder.DropTable(
                name: "grocery_staples");

            migrationBuilder.DropTable(
                name: "manual_grocery_items");

            migrationBuilder.DropTable(
                name: "user_grocery_preferences");
        }
    }
}
