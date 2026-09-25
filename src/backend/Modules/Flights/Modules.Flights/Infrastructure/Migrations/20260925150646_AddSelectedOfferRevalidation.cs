using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Flights.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedOfferRevalidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AcceptedPriceQuoteId",
                schema: "flights",
                table: "SelectedOffers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConfirmedAmount",
                schema: "flights",
                table: "SelectedOffers",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmedCurrency",
                schema: "flights",
                table: "SelectedOffers",
                type: "char(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PriceQuoteId",
                schema: "flights",
                table: "SelectedOffers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuotedAmount",
                schema: "flights",
                table: "SelectedOffers",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuotedCurrency",
                schema: "flights",
                table: "SelectedOffers",
                type: "char(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevalidatedAt",
                schema: "flights",
                table: "SelectedOffers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "flights",
                table: "SelectedOffers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Selected"); // Rows selected before revalidation existed.

            migrationBuilder.AddCheckConstraint(
                name: "CK_SelectedOffers_ConfirmedPrice",
                schema: "flights",
                table: "SelectedOffers",
                sql: "([ConfirmedAmount] IS NULL AND [ConfirmedCurrency] IS NULL) OR ([ConfirmedAmount] IS NOT NULL AND [ConfirmedCurrency] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SelectedOffers_QuotedPrice",
                schema: "flights",
                table: "SelectedOffers",
                sql: "([QuotedAmount] IS NULL AND [QuotedCurrency] IS NULL) OR ([QuotedAmount] IS NOT NULL AND [QuotedCurrency] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SelectedOffers_Status",
                schema: "flights",
                table: "SelectedOffers",
                sql: "[Status] IN ('Selected', 'Confirmed', 'PriceChanged', 'Expired', 'SoldOut')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SelectedOffers_ConfirmedPrice",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SelectedOffers_QuotedPrice",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SelectedOffers_Status",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "AcceptedPriceQuoteId",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "ConfirmedAmount",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "ConfirmedCurrency",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "PriceQuoteId",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "QuotedAmount",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "QuotedCurrency",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "RevalidatedAt",
                schema: "flights",
                table: "SelectedOffers");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "flights",
                table: "SelectedOffers");
        }
    }
}
