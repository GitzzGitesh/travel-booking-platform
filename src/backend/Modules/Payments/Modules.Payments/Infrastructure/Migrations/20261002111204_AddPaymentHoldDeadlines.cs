using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentHoldDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuthorizedAt",
                schema: "payments",
                table: "PaymentAttempts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HoldWarningRaisedAt",
                schema: "payments",
                table: "PaymentAttempts",
                type: "datetimeoffset",
                nullable: true);

            // Attempts authorized before this migration: their first move to Authorized, from their own history (attempts
            // that never showed Authorized fall back to their start time in code). In EXEC so a single-batch script
            // compiles it only after the column exists.
            migrationBuilder.Sql(
                """
                EXEC(N'UPDATE a SET a.AuthorizedAt = (
                    SELECT MIN(e.[At]) FROM [payments].[PaymentAttemptEvents] e
                    WHERE e.PaymentAttemptId = a.Id AND e.ToStatus = N''Authorized'')
                FROM [payments].[PaymentAttempts] a
                WHERE a.AuthorizedAt IS NULL;');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_HoldsToWarn",
                schema: "payments",
                table: "PaymentAttempts",
                columns: new[] { "Status", "AuthorizedAt", "CreatedAt" },
                filter: "[HoldWarningRaisedAt] IS NULL AND [Status] IN ('Authorized','Capturing','CaptureUnknown','ManualReview','Voiding','VoidUnknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_HoldsToWarn",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "AuthorizedAt",
                schema: "payments",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "HoldWarningRaisedAt",
                schema: "payments",
                table: "PaymentAttempts");
        }
    }
}
