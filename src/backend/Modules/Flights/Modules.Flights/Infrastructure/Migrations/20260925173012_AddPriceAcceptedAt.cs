using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Flights.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceAcceptedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PriceAcceptedAt",
                schema: "flights",
                table: "SelectedOffers",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceAcceptedAt",
                schema: "flights",
                table: "SelectedOffers");
        }
    }
}
