using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantPOS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MultipleIngredientSelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OptionGroupSelections",
                table: "MenuOptionGroups");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OptionGroupSelections",
                table: "MenuOptionGroups",
                sql: "MaxSelections BETWEEN 1 AND 20");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OptionGroupSelections",
                table: "MenuOptionGroups");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OptionGroupSelections",
                table: "MenuOptionGroups",
                sql: "MaxSelections BETWEEN 1 AND 2");
        }
    }
}
