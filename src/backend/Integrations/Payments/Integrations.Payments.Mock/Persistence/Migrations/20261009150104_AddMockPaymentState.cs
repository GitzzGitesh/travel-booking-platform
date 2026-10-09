using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelBooking.Integrations.Payments.Mock.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMockPaymentState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "paymentsmock");

            migrationBuilder.CreateTable(
                name: "FailedOnce",
                schema: "paymentsmock",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailedOnce", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Operations",
                schema: "paymentsmock",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "paymentsmock",
                columns: table => new
                {
                    Reference = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ProviderRef = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false),
                    Method = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Captured = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Refunded = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    DeclineReason = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    ActionToken = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Reference);
                });

            migrationBuilder.CreateTable(
                name: "Refunds",
                schema: "paymentsmock",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Reference = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ProviderRefundRef = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FailedOnce",
                schema: "paymentsmock");

            migrationBuilder.DropTable(
                name: "Operations",
                schema: "paymentsmock");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "paymentsmock");

            migrationBuilder.DropTable(
                name: "Refunds",
                schema: "paymentsmock");
        }
    }
}
