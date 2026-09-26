using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Flights.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedOfferFare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FareJson",
                schema: "flights",
                table: "SelectedOffers",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FareJson",
                schema: "flights",
                table: "SelectedOffers");
        }
    }
}
