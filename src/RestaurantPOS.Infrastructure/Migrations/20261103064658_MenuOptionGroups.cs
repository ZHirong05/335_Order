using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantPOS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MenuOptionGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MenuCatalogImports",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuCatalogImports", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "MenuOptionGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MenuItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuOptionGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuOptionGroups_Menu_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "Menu",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MenuOptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MenuOptionGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    AdditionalPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuOptions", x => x.Id);
                    table.CheckConstraint("CK_OptionPrice", "AdditionalPrice >= 0");
                    table.ForeignKey(
                        name: "FK_MenuOptions_MenuOptionGroups_MenuOptionGroupId",
                        column: x => x.MenuOptionGroupId,
                        principalTable: "MenuOptionGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MenuOptionGroups_MenuItemId_Name",
                table: "MenuOptionGroups",
                columns: new[] { "MenuItemId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MenuOptions_MenuOptionGroupId_Name",
                table: "MenuOptions",
                columns: new[] { "MenuOptionGroupId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MenuCatalogImports");

            migrationBuilder.DropTable(
                name: "MenuOptions");

            migrationBuilder.DropTable(
                name: "MenuOptionGroups");
        }
    }
}
