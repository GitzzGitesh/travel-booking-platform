using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FlightOrderItems_Status",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "orders",
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
                name: "RefundCases",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ItemIds = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    SupplierRefund = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Fee = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    SupplierReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RequestedByAccount = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "varchar(1200)", unicode: false, maxLength: 1200, nullable: false),
                    OverdueAlertedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundCases", x => x.Id);
                    table.CheckConstraint("CK_RefundCases_Status", "[Status] IN ('PendingApproval', 'Approved', 'Rejected', 'Refunded', 'RefundFailed', 'NoRefund')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_FlightOrderItems_Status",
                schema: "orders",
                table: "FlightOrderItems",
                sql: "[Status] IN ('Draft', 'AwaitingPayment', 'Abandoned', 'Booking', 'PendingConfirmation', 'ManualReview', 'Confirmed', 'Failed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_RefundCases_OrderId",
                schema: "orders",
                table: "RefundCases",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundCases_RequestedBy_IdempotencyKey",
                schema: "orders",
                table: "RefundCases",
                columns: new[] { "RequestedBy", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundCases_Status_RequestedAt",
                schema: "orders",
                table: "RefundCases",
                columns: new[] { "Status", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "RefundCases",
                schema: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FlightOrderItems_Status",
                schema: "orders",
                table: "FlightOrderItems");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FlightOrderItems_Status",
                schema: "orders",
                table: "FlightOrderItems",
                sql: "[Status] IN ('Draft', 'AwaitingPayment', 'Abandoned', 'Booking', 'PendingConfirmation', 'ManualReview', 'Confirmed', 'Failed')");
        }
    }
}
