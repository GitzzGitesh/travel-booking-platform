using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_IdempotencyKey",
                schema: "orders",
                table: "Orders");

            migrationBuilder.AddColumn<string>(
                name: "CustomerId",
                schema: "orders",
                table: "Orders",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CustomerId_IdempotencyKey",
                schema: "orders",
                table: "Orders",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_CustomerId_IdempotencyKey",
                schema: "orders",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                schema: "orders",
                table: "Orders");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_IdempotencyKey",
                schema: "orders",
                table: "Orders",
                column: "IdempotencyKey",
                unique: true);
        }
    }
}
