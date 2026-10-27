using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FateAndFridge.Api.Migrations
{
    /// <inheritdoc />
    public partial class M3RecipeSchemaBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recipes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    source_site = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    normalized_source_url = table.Column<string>(type: "text", nullable: true),
                    author = table.Column<string>(type: "text", nullable: true),
                    prep_minutes = table.Column<int>(type: "integer", nullable: true),
                    cook_minutes = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    is_favorite = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "normal"),
                    is_vegan = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_vegetarian = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recipe_images",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipe_id = table.Column<long>(type: "bigint", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    alt_text = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_recipe_images_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipe_ingredient_groups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipe_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_ingredient_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_recipe_ingredient_groups_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipe_instruction_sections",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipe_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_instruction_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_recipe_instruction_sections_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipe_proteins",
                columns: table => new
                {
                    recipe_id = table.Column<long>(type: "bigint", nullable: false),
                    protein_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_proteins", x => new { x.recipe_id, x.protein_id });
                    table.ForeignKey(
                        name: "fk_recipe_proteins_proteins_protein_id",
                        column: x => x.protein_id,
                        principalTable: "proteins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recipe_proteins_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipe_tags",
                columns: table => new
                {
                    recipe_id = table.Column<long>(type: "bigint", nullable: false),
                    tag_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_tags", x => new { x.recipe_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_recipe_tags_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recipe_tags_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recipe_ingredients",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipe_id = table.Column<long>(type: "bigint", nullable: false),
                    group_id = table.Column<long>(type: "bigint", nullable: true),
                    ingredient_id = table.Column<long>(type: "bigint", nullable: false),
                    quantity_numeric = table.Column<decimal>(type: "numeric(28,14)", precision: 28, scale: 14, nullable: true),
                    unit_id = table.Column<long>(type: "bigint", nullable: true),
                    preparation_note = table.Column<string>(type: "text", nullable: true),
                    is_optional = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_ingredients", x => x.id);
                    table.ForeignKey(
                        name: "fk_recipe_ingredients_ingredients_ingredient_id",
                        column: x => x.ingredient_id,
                        principalTable: "ingredients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recipe_ingredients_recipe_ingredient_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "recipe_ingredient_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recipe_ingredients_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recipe_ingredients_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recipe_instruction_steps",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    section_id = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_instruction_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_recipe_instruction_steps_recipe_instruction_sections_sectio",
                        column: x => x.section_id,
                        principalTable: "recipe_instruction_sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_recipe_images_recipe_id",
                table: "recipe_images",
                column: "recipe_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recipe_ingredient_groups_recipe_id",
                table: "recipe_ingredient_groups",
                column: "recipe_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_ingredients_group_id",
                table: "recipe_ingredients",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_ingredients_ingredient_id",
                table: "recipe_ingredients",
                column: "ingredient_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_ingredients_recipe_id",
                table: "recipe_ingredients",
                column: "recipe_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_ingredients_unit_id",
                table: "recipe_ingredients",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_instruction_sections_recipe_id",
                table: "recipe_instruction_sections",
                column: "recipe_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_instruction_steps_section_id",
                table: "recipe_instruction_steps",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_proteins_protein_id",
                table: "recipe_proteins",
                column: "protein_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipe_tags_tag_id",
                table: "recipe_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipes_normalized_name",
                table: "recipes",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recipes_normalized_source_url",
                table: "recipes",
                column: "normalized_source_url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recipe_images");

            migrationBuilder.DropTable(
                name: "recipe_ingredients");

            migrationBuilder.DropTable(
                name: "recipe_instruction_steps");

            migrationBuilder.DropTable(
                name: "recipe_proteins");

            migrationBuilder.DropTable(
                name: "recipe_tags");

            migrationBuilder.DropTable(
                name: "recipe_ingredient_groups");

            migrationBuilder.DropTable(
                name: "recipe_instruction_sections");

            migrationBuilder.DropTable(
                name: "recipes");
        }
    }
}
