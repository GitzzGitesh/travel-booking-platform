using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTravellerNeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DocumentsRequired",
                schema: "orders",
                table: "FlightOrderItems",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastTravelDate",
                schema: "orders",
                table: "FlightOrderItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TravellerAdults",
                schema: "orders",
                table: "FlightOrderItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TravellerChildren",
                schema: "orders",
                table: "FlightOrderItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TravellerInfants",
                schema: "orders",
                table: "FlightOrderItems",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentsRequired",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "LastTravelDate",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "TravellerAdults",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "TravellerChildren",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.DropColumn(
                name: "TravellerInfants",
                schema: "orders",
                table: "FlightOrderItems");
        }
    }
}
