using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttemptLimitTrips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttemptLimitTrips",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttemptLimitTrips", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttemptLimitTrips_At",
                schema: "payments",
                table: "AttemptLimitTrips",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_AttemptLimitTrips_CustomerId_At",
                schema: "payments",
                table: "AttemptLimitTrips",
                columns: new[] { "CustomerId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AttemptLimitTrips_CustomerId_IdempotencyKey",
                schema: "payments",
                table: "AttemptLimitTrips",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttemptLimitTrips",
                schema: "payments");
        }
    }
}
