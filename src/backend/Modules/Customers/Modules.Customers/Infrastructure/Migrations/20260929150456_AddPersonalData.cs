using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentAccessLog",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentAccessLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobLeases",
                schema: "customers",
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
                name: "RetentionEvents",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TravelDocuments",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TravellerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    WrappedKey = table.Column<byte[]>(type: "varbinary(128)", maxLength: 128, nullable: true),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(1024)", maxLength: 1024, nullable: true),
                    StoredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ShreddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TravellerSets",
                schema: "customers",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    ContactPhone = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    LastTravelDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RetainUntil = table.Column<DateOnly>(type: "date", nullable: false),
                    DocumentsRetainUntil = table.Column<DateOnly>(type: "date", nullable: false),
                    AnonymisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LegalHold = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravellerSets", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "Travellers",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    GivenNames = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Surname = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Travellers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Travellers_TravellerSets_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "customers",
                        principalTable: "TravellerSets",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAccessLog_DocumentId_Id",
                schema: "customers",
                table: "DocumentAccessLog",
                columns: new[] { "DocumentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionEvents_OrderId_Id",
                schema: "customers",
                table: "RetentionEvents",
                columns: new[] { "OrderId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TravelDocuments_OrderId_ShreddedAt",
                schema: "customers",
                table: "TravelDocuments",
                columns: new[] { "OrderId", "ShreddedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Travellers_OrderId_Position",
                schema: "customers",
                table: "Travellers",
                columns: new[] { "OrderId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravellerSets_LegalHold_DocumentsRetainUntil",
                schema: "customers",
                table: "TravellerSets",
                columns: new[] { "LegalHold", "DocumentsRetainUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_TravellerSets_LegalHold_RetainUntil",
                schema: "customers",
                table: "TravellerSets",
                columns: new[] { "LegalHold", "RetainUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentAccessLog",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "JobLeases",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "RetentionEvents",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "TravelDocuments",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "Travellers",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "TravellerSets",
                schema: "customers");
        }
    }
}
