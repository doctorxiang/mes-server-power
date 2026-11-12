using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MesApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrderIssuedQty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IssuedQty",
                table: "work_order",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // 老工单没有发料记录：先按"计划量已发"回填，避免界面上发料显示 0；
            // 需要冗余料的单子走补料单追加（IssuedQty 会跟着变大）
            migrationBuilder.Sql("UPDATE work_order SET IssuedQty = PlanQty WHERE IssuedQty = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IssuedQty",
                table: "work_order");
        }
    }
}
