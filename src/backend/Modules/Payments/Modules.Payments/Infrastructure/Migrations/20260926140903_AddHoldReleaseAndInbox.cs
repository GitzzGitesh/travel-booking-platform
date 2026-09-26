using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldReleaseAndInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_OrderId_Live",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentAttempts_Status",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.AddColumn<string>(
                name: "ReleaseReason",
                schema: "payments",
                table: "PaymentAttempts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleaseRequestedAt",
                schema: "payments",
                table: "PaymentAttempts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "payments",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Handler = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.MessageId, x.Handler });
                });

            migrationBuilder.CreateTable(
                name: "JobLeases",
                schema: "payments",
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

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId_Live",
                schema: "payments",
                table: "PaymentAttempts",
                column: "OrderId",
                unique: true,
                filter: "[Status] IN ('Authorizing', 'ActionRequired', 'AuthorizationUnknown', 'Authorized', 'ManualReview', 'Voiding', 'VoidUnknown')");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_Status_UpdatedAt",
                schema: "payments",
                table: "PaymentAttempts",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentAttempts_Status",
                schema: "payments",
                table: "PaymentAttempts",
                sql: "[Status] IN ('Authorizing', 'ActionRequired', 'AuthorizationUnknown', 'Authorized', 'Declined', 'Canceled', 'Expired', 'Failed', 'ManualReview', 'Voiding', 'VoidUnknown', 'Voided')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "JobLeases",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_OrderId_Live",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_Status_UpdatedAt",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentAttempts_Status",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "ReleaseReason",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "ReleaseRequestedAt",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId_Live",
                schema: "payments",
                table: "PaymentAttempts",
                column: "OrderId",
                unique: true,
                filter: "[Status] IN ('Authorizing', 'ActionRequired', 'AuthorizationUnknown', 'Authorized', 'ManualReview')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentAttempts_Status",
                schema: "payments",
                table: "PaymentAttempts",
                sql: "[Status] IN ('Authorizing', 'ActionRequired', 'AuthorizationUnknown', 'Authorized', 'Declined', 'Canceled', 'Expired', 'Failed', 'ManualReview')");
        }
    }
}
