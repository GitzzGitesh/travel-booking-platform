using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierLocatorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_FlightOrderItems_SupplierLocator",
                schema: "orders",
                table: "FlightOrderItems",
                column: "SupplierLocator",
                filter: "[SupplierLocator] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FlightOrderItems_SupplierLocator",
                schema: "orders",
                table: "FlightOrderItems");
        }
    }
}
