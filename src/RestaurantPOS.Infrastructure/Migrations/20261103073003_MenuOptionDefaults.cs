using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantPOS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MenuOptionDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "MenuOptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MenuOptions_MenuOptionGroupId",
                table: "MenuOptions",
                column: "MenuOptionGroupId",
                unique: true,
                filter: "IsDefault = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MenuOptions_MenuOptionGroupId",
                table: "MenuOptions");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "MenuOptions");
        }
    }
}
