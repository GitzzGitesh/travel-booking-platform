using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentAttempts_Status",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.AddColumn<decimal>(
                name: "CaptureAmount",
                schema: "payments",
                table: "PaymentAttempts",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CaptureRequestedAt",
                schema: "payments",
                table: "PaymentAttempts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentAttempts_Status",
                schema: "payments",
                table: "PaymentAttempts",
                sql: "[Status] IN ('Authorizing', 'ActionRequired', 'AuthorizationUnknown', 'Authorized', 'Declined', 'Canceled', 'Expired', 'Failed', 'ManualReview', 'Voiding', 'VoidUnknown', 'Voided', 'Capturing', 'CaptureUnknown', 'Captured')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentAttempts_Status",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "CaptureAmount",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "CaptureRequestedAt",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentAttempts_Status",
                schema: "payments",
                table: "PaymentAttempts",
                sql: "[Status] IN ('Authorizing', 'ActionRequired', 'AuthorizationUnknown', 'Authorized', 'Declined', 'Canceled', 'Expired', 'Failed', 'ManualReview', 'Voiding', 'VoidUnknown', 'Voided')");
        }
    }
}
