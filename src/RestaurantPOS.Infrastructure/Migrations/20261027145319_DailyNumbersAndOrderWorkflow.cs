using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantPOS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DailyNumbersAndOrderWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessDate",
                table: "Orders",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DailyNumber",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "KitchenTicketItem",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Preserve existing GUIDs/references and backfill daily display numbers using Malaysia time.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT Id,
                           strftime('%Y-%m-%d', CreatedAtUtc, '+8 hours') AS Day,
                           ROW_NUMBER() OVER (
                               PARTITION BY strftime('%Y-%m-%d', CreatedAtUtc, '+8 hours')
                               ORDER BY CreatedAtUtc, Id
                           ) AS Sequence
                    FROM Orders
                )
                UPDATE Orders
                SET BusinessDate = (SELECT Day FROM numbered WHERE numbered.Id = Orders.Id),
                    DailyNumber = (SELECT Sequence FROM numbered WHERE numbered.Id = Orders.Id);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessDate_DailyNumber",
                table: "Orders",
                columns: new[] { "BusinessDate", "DailyNumber" },
                unique: true,
                filter: "DailyNumber > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_BusinessDate_DailyNumber",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BusinessDate",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DailyNumber",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "KitchenTicketItem");
        }
    }
}
