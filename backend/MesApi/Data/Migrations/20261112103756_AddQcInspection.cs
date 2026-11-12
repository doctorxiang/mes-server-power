using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MesApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQcInspection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "qc_inspection",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    SerialUnitId = table.Column<long>(type: "bigint", nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Result = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DefectCodeId = table.Column<int>(type: "int", nullable: true),
                    Remark = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InspectorId = table.Column<int>(type: "int", nullable: false),
                    InspectorName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProductionDate = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InspectedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qc_inspection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_qc_inspection_defect_code_DefectCodeId",
                        column: x => x.DefectCodeId,
                        principalTable: "defect_code",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_qc_inspection_serial_unit_SerialUnitId",
                        column: x => x.SerialUnitId,
                        principalTable: "serial_unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspection_DefectCodeId",
                table: "qc_inspection",
                column: "DefectCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspection_ProductionDate_Result",
                table: "qc_inspection",
                columns: new[] { "ProductionDate", "Result" });

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspection_SerialUnitId",
                table: "qc_inspection",
                column: "SerialUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspection_WorkOrderId_InspectedAtUtc",
                table: "qc_inspection",
                columns: new[] { "WorkOrderId", "InspectedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "qc_inspection");
        }
    }
}
