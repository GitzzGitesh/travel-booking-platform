using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHotelCancellationTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CancellationPenaltyAmount",
                schema: "orders",
                table: "FlightOrderItems",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CancellationRefundable",
                schema: "orders",
                table: "FlightOrderItems",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FreeCancellationUntil",
                schema: "orders",
                table: "FlightOrderItems",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationPenaltyAmount",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "CancellationRefundable",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "FreeCancellationUntil",
                schema: "orders",
                table: "FlightOrderItems");
        }
    }
}
