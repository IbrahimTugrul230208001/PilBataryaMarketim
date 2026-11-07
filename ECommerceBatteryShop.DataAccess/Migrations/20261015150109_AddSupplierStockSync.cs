using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ECommerceBatteryShop.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierStockSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    FeedFormat = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MinExpectedItems = table.Column<int>(type: "integer", nullable: false),
                    StaleAfterHours = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductSupplierLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    SupplierSku = table.Column<string>(type: "text", nullable: false),
                    LastQuantity = table.Column<int>(type: "integer", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSupplierLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductSupplierLinks_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductSupplierLinks_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockSyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    FeedItemCount = table.Column<int>(type: "integer", nullable: false),
                    MatchedCount = table.Column<int>(type: "integer", nullable: false),
                    MissingCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockSyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockSyncRuns_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Suppliers",
                columns: new[] { "Id", "Code", "FeedFormat", "IsEnabled", "MinExpectedItems", "Name", "StaleAfterHours" },
                values: new object[] { 1, "pilmagazasi", "akakce-xml", true, 300, "Pil Mağazası", 48 });

            migrationBuilder.CreateIndex(
                name: "IX_ProductSupplierLinks_ProductId_SupplierId",
                table: "ProductSupplierLinks",
                columns: new[] { "ProductId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductSupplierLinks_SupplierId_SupplierSku",
                table: "ProductSupplierLinks",
                columns: new[] { "SupplierId", "SupplierSku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockSyncRuns_SupplierId_StartedAt",
                table: "StockSyncRuns",
                columns: new[] { "SupplierId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_Code",
                table: "Suppliers",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductSupplierLinks");

            migrationBuilder.DropTable(
                name: "StockSyncRuns");

            migrationBuilder.DropTable(
                name: "Suppliers");
        }
    }
}
