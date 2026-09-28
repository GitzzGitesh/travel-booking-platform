using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Flights.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectionOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SelectedOffers_SearchId_OfferId",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.AddColumn<string>(
                name: "CustomerId",
                schema: "flights",
                table: "SelectedOffers",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelectedOffers_SearchId_OfferId_CustomerId",
                schema: "flights",
                table: "SelectedOffers",
                columns: new[] { "SearchId", "OfferId", "CustomerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SelectedOffers_SearchId_OfferId_CustomerId",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.CreateIndex(
                name: "IX_SelectedOffers_SearchId_OfferId",
                schema: "flights",
                table: "SelectedOffers",
                columns: new[] { "SearchId", "OfferId" },
                unique: true);
        }
    }
}
