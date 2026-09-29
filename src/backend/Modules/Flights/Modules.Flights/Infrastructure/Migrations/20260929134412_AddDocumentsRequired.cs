using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Flights.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentsRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DocumentsRequired",
                schema: "flights",
                table: "SelectedOffers",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentsRequired",
                schema: "flights",
                table: "SelectedOffers");
        }
    }
}
