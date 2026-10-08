using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Hotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookedSelectionStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Selections_Status",
                schema: "hotels",
                table: "Selections");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Selections_Status",
                schema: "hotels",
                table: "Selections",
                sql: "[Status] IN ('Selected', 'Confirmed', 'PriceChanged', 'Expired', 'SoldOut', 'Booking', 'Booked')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Selections_Status",
                schema: "hotels",
                table: "Selections");

            // Rolling back: a frozen selection becomes Confirmed again (the old states cannot say Booking or Booked).
            migrationBuilder.Sql("UPDATE [hotels].[Selections] SET [Status] = 'Confirmed' WHERE [Status] IN ('Booking', 'Booked')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Selections_Status",
                schema: "hotels",
                table: "Selections",
                sql: "[Status] IN ('Selected', 'Confirmed', 'PriceChanged', 'Expired', 'SoldOut')");
        }
    }
}
