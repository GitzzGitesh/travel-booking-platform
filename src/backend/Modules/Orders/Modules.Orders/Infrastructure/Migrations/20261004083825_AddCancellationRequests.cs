using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCancellationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CancellationRequests",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(138)", maxLength: 138, nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CancellationRequests", x => x.Id);
                    table.CheckConstraint("CK_CancellationRequests_Status", "[Status] IN ('Open', 'Completed', 'Declined', 'Withdrawn')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CustomerId_CreatedAt_Id",
                schema: "orders",
                table: "Orders",
                columns: new[] { "CustomerId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CancellationRequests_CustomerId_IdempotencyKey",
                schema: "orders",
                table: "CancellationRequests",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CancellationRequests_OrderId_Open",
                schema: "orders",
                table: "CancellationRequests",
                column: "OrderId",
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_CancellationRequests_Status_RequestedAt",
                schema: "orders",
                table: "CancellationRequests",
                columns: new[] { "Status", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CancellationRequests",
                schema: "orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CustomerId_CreatedAt_Id",
                schema: "orders",
                table: "Orders");
        }
    }
}
