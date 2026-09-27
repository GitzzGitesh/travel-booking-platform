using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentNotifications",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EventId = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reference = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    ProviderPaymentId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    EventType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_ProviderId_ProviderPaymentId",
                schema: "payments",
                table: "PaymentAttempts",
                columns: new[] { "ProviderId", "ProviderPaymentId" },
                unique: true,
                filter: "[ProviderPaymentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentNotifications_ProcessedAt_ReceivedAt",
                schema: "payments",
                table: "PaymentNotifications",
                columns: new[] { "ProcessedAt", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentNotifications_ProviderId_EventId",
                schema: "payments",
                table: "PaymentNotifications",
                columns: new[] { "ProviderId", "EventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentNotifications",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_ProviderId_ProviderPaymentId",
                schema: "payments",
                table: "PaymentAttempts");
        }
    }
}
