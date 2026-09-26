using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxAndJobLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobLeases",
                schema: "orders",
                columns: table => new
                {
                    Name = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobLeases", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TraceParent = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlightOrderItems_Status_OfferExpiresAt",
                schema: "orders",
                table: "FlightOrderItems",
                columns: new[] { "Status", "OfferExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_NextAttemptAt_OccurredAt",
                schema: "orders",
                table: "OutboxMessages",
                columns: new[] { "NextAttemptAt", "OccurredAt" },
                filter: "[ProcessedAt] IS NULL AND [FailedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobLeases",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "orders");

            migrationBuilder.DropIndex(
                name: "IX_FlightOrderItems_Status_OfferExpiresAt",
                schema: "orders",
                table: "FlightOrderItems");
        }
    }
}
