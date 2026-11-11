using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MesApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepairStation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repair_record",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    SerialUnitId = table.Column<long>(type: "bigint", nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    RepairNo = table.Column<int>(type: "int", nullable: false),
                    SourceStepId = table.Column<int>(type: "int", nullable: false),
                    DefectCodeId = table.Column<int>(type: "int", nullable: true),
                    Action = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Remark = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OperatorId = table.Column<int>(type: "int", nullable: false),
                    OperatorName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StationCode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CheckoutByName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repair_record", x => x.Id);
                    table.ForeignKey(
                        name: "FK_repair_record_defect_code_DefectCodeId",
                        column: x => x.DefectCodeId,
                        principalTable: "defect_code",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_repair_record_process_step_SourceStepId",
                        column: x => x.SourceStepId,
                        principalTable: "process_step",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_repair_record_serial_unit_SerialUnitId",
                        column: x => x.SerialUnitId,
                        principalTable: "serial_unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_repair_record_DefectCodeId",
                table: "repair_record",
                column: "DefectCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_repair_record_SerialUnitId_RepairNo",
                table: "repair_record",
                columns: new[] { "SerialUnitId", "RepairNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repair_record_SourceStepId",
                table: "repair_record",
                column: "SourceStepId");

            migrationBuilder.CreateIndex(
                name: "IX_repair_record_Status_StartedAtUtc",
                table: "repair_record",
                columns: new[] { "Status", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repair_record");
        }
    }
}
