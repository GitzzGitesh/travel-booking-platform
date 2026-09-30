using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentSettlementRequestedAt",
                schema: "orders",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BookingLookups",
                schema: "orders",
                table: "FlightOrderItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BookingStartedAt",
                schema: "orders",
                table: "FlightOrderItems",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextBookingLookupAt",
                schema: "orders",
                table: "FlightOrderItems",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ticketing",
                schema: "orders",
                table: "FlightOrderItems",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlightOrderItems_Status_NextBookingLookupAt",
                schema: "orders",
                table: "FlightOrderItems",
                columns: new[] { "Status", "NextBookingLookupAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FlightOrderItems_Status_NextBookingLookupAt",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "PaymentSettlementRequestedAt",
                schema: "orders",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BookingLookups",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "BookingStartedAt",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "NextBookingLookupAt",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "Ticketing",
                schema: "orders",
                table: "FlightOrderItems");
        }
    }
}
