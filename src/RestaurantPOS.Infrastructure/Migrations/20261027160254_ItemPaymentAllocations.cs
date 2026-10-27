using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantPOS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ItemPaymentAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAllocations", x => x.Id);
                    table.CheckConstraint("CK_PaymentAllocation", "Quantity > 0 AND Amount >= 0");
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_OrderItems_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "OrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_OrderItemId",
                table: "PaymentAllocations",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_PaymentId_OrderItemId",
                table: "PaymentAllocations",
                columns: new[] { "PaymentId", "OrderItemId" },
                unique: true);

            // Legacy checkout recorded one complete payment per closed order.
            migrationBuilder.Sql("""
                INSERT INTO PaymentAllocations (Id, PaymentId, OrderItemId, Quantity, Amount)
                SELECT hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6)),
                       p.Id, i.Id, i.Quantity - i.CancelledQuantity,
                       (i.Quantity - i.CancelledQuantity) * i.UnitPriceSnapshot
                FROM Payments p JOIN Orders o ON o.Id = p.OrderId JOIN OrderItems i ON i.OrderId = o.Id
                WHERE o.Status = 2 AND i.Quantity > i.CancelledQuantity
                  AND (SELECT COUNT(*) FROM Payments other WHERE other.OrderId = o.Id) = 1
                  AND p.Amount = (SELECT COALESCE(SUM((Quantity - CancelledQuantity) * UnitPriceSnapshot), 0) FROM OrderItems WHERE OrderId = o.Id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentAllocations");
        }
    }
}
