using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MesApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformStorageAndScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TaktSeconds",
                table: "process_step",
                type: "int",
                nullable: false,
                defaultValue: 60);

            // 老库补节拍：APS 依赖该字段，全 0 会算出产能无穷大，按服务器电源实际工艺回填
            migrationBuilder.Sql("""
                UPDATE process_step SET TaktSeconds = CASE Code
                    WHEN 'PLUGIN'  THEN 60
                    WHEN 'WAVE'    THEN 45
                    WHEN 'TOUCHUP' THEN 40
                    WHEN 'VISUAL'  THEN 35
                    WHEN 'ICT'     THEN 50
                    WHEN 'BURNIN'  THEN 90
                    WHEN 'HIPOT'   THEN 30
                    WHEN 'FCT'     THEN 45
                    WHEN 'PACK'    THEN 40
                    ELSE 60 END
                WHERE TaktSeconds = 0;
                """);

            migrationBuilder.CreateTable(
                name: "daily_report",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ReportDate = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    InputQty = table.Column<int>(type: "int", nullable: false),
                    CompletedQty = table.Column<int>(type: "int", nullable: false),
                    FailQty = table.Column<int>(type: "int", nullable: false),
                    YieldTenths = table.Column<int>(type: "int", nullable: false),
                    TopDefectsJson = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_report", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "file_document",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Category = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    WorkOrderId = table.Column<int>(type: "int", nullable: true),
                    Sn = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FileName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ObjectKey = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Remark = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UploadedByUserId = table.Column<int>(type: "int", nullable: false),
                    UploadedByName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_document", x => x.Id);
                    table.ForeignKey(
                        name: "FK_file_document_product_ProductId",
                        column: x => x.ProductId,
                        principalTable: "product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_file_document_work_order_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_daily_report_ReportDate_LineId",
                table: "daily_report",
                columns: new[] { "ReportDate", "LineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_document_Category_CreatedAtUtc",
                table: "file_document",
                columns: new[] { "Category", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_file_document_ProductId",
                table: "file_document",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_file_document_Sn",
                table: "file_document",
                column: "Sn");

            migrationBuilder.CreateIndex(
                name: "IX_file_document_WorkOrderId",
                table: "file_document",
                column: "WorkOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_report");

            migrationBuilder.DropTable(
                name: "file_document");

            migrationBuilder.DropColumn(
                name: "TaktSeconds",
                table: "process_step");
        }
    }
}
