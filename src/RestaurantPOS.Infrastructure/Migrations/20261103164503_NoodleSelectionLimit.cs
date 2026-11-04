using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantPOS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NoodleSelectionLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxSelections",
                table: "MenuOptionGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OptionGroupSelections",
                table: "MenuOptionGroups",
                sql: "MaxSelections BETWEEN 1 AND 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OptionGroupSelections",
                table: "MenuOptionGroups");

            migrationBuilder.DropColumn(
                name: "MaxSelections",
                table: "MenuOptionGroups");
        }
    }
}
