using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderItemProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Product",
                schema: "orders",
                table: "FlightOrderItems",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: false,
                defaultValue: "Flight"); // every existing item books a flight (ADR 0030 §6)

            migrationBuilder.AddCheckConstraint(
                name: "CK_FlightOrderItems_Product",
                schema: "orders",
                table: "FlightOrderItems",
                sql: "[Product] IN ('Flight', 'Hotel')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FlightOrderItems_Product",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "Product",
                schema: "orders",
                table: "FlightOrderItems");
        }
    }
}
