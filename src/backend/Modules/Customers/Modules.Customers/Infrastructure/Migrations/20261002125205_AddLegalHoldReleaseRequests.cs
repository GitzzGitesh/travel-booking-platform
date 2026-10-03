using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalHoldReleaseRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "PurgeNotBefore",
                schema: "customers",
                table: "TravellerSets",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LegalHoldReleaseRequests",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RequestedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RequestedByAccount = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalHoldReleaseRequests", x => x.Id);
                    table.CheckConstraint("CK_LegalHoldReleaseRequests_Status", "[Status] IN ('Pending','Approved','Rejected')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegalHoldReleaseRequests_OrderId_Pending",
                schema: "customers",
                table: "LegalHoldReleaseRequests",
                column: "OrderId",
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_LegalHoldReleaseRequests_Status_RequestedAt",
                schema: "customers",
                table: "LegalHoldReleaseRequests",
                columns: new[] { "Status", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegalHoldReleaseRequests",
                schema: "customers");

            migrationBuilder.DropColumn(
                name: "PurgeNotBefore",
                schema: "customers",
                table: "TravellerSets");
        }
    }
}
