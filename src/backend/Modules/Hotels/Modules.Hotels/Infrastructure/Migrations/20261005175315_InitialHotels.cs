using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Hotels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialHotels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hotels");

            migrationBuilder.CreateTable(
                name: "Selections",
                schema: "hotels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    SearchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProviderOfferToken = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Destination = table.Column<string>(type: "char(3)", nullable: false),
                    CheckIn = table.Column<DateOnly>(type: "date", nullable: false),
                    CheckOut = table.Column<DateOnly>(type: "date", nullable: false),
                    Adults = table.Column<int>(type: "int", nullable: false),
                    ChildAges = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    PropertyId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PropertyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressLine = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CityCode = table.Column<string>(type: "char(3)", nullable: false),
                    CountryCode = table.Column<string>(type: "char(2)", nullable: false),
                    StarRating = table.Column<decimal>(type: "decimal(2,1)", precision: 2, scale: 1, nullable: true),
                    TimeZone = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RoomDescription = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Board = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Refundable = table.Column<bool>(type: "bit", nullable: false),
                    FreeCancellationUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OfferExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RevalidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PriceQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TermsChanged = table.Column<bool>(type: "bit", nullable: false),
                    AcceptedPriceQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PriceAcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FeesAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    FeesCurrency = table.Column<string>(type: "char(3)", nullable: true),
                    PenaltyAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PenaltyCurrency = table.Column<string>(type: "char(3)", nullable: true),
                    ConfirmedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ConfirmedCurrency = table.Column<string>(type: "char(3)", nullable: true),
                    QuotedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    QuotedCurrency = table.Column<string>(type: "char(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Selections", x => x.Id);
                    table.CheckConstraint("CK_Selections_ConfirmedPrice", "([ConfirmedAmount] IS NULL AND [ConfirmedCurrency] IS NULL) OR ([ConfirmedAmount] IS NOT NULL AND [ConfirmedCurrency] IS NOT NULL)");
                    table.CheckConstraint("CK_Selections_FeesPrice", "([FeesAmount] IS NULL AND [FeesCurrency] IS NULL) OR ([FeesAmount] IS NOT NULL AND [FeesCurrency] IS NOT NULL)");
                    table.CheckConstraint("CK_Selections_PenaltyPrice", "([PenaltyAmount] IS NULL AND [PenaltyCurrency] IS NULL) OR ([PenaltyAmount] IS NOT NULL AND [PenaltyCurrency] IS NOT NULL)");
                    table.CheckConstraint("CK_Selections_QuotedPrice", "([QuotedAmount] IS NULL AND [QuotedCurrency] IS NULL) OR ([QuotedAmount] IS NOT NULL AND [QuotedCurrency] IS NOT NULL)");
                    table.CheckConstraint("CK_Selections_Status", "[Status] IN ('Selected', 'Confirmed', 'PriceChanged', 'Expired', 'SoldOut')");
                    table.CheckConstraint("CK_Selections_Stay", "[CheckOut] > [CheckIn]");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Selections_SearchId_OfferId_CustomerId",
                schema: "hotels",
                table: "Selections",
                columns: new[] { "SearchId", "OfferId", "CustomerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Selections",
                schema: "hotels");
        }
    }
}
