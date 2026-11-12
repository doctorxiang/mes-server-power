using MesApi.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MesApi.Data;

public class MesDbContext(DbContextOptions<MesDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<ProductionLine> Lines => Set<ProductionLine>();
    public DbSet<Workstation> Workstations => Set<Workstation>();
    public DbSet<ProcessStep> Steps => Set<ProcessStep>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductRoute> ProductRoutes => Set<ProductRoute>();
    public DbSet<DefectCode> DefectCodes => Set<DefectCode>();

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<SerialUnit> Units => Set<SerialUnit>();
    public DbSet<SnMoveLog> MoveLogs => Set<SnMoveLog>();
    public DbSet<RepairRecord> RepairRecords => Set<RepairRecord>();
    public DbSet<ExceptionEvent> Exceptions => Set<ExceptionEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<FileDocument> FileDocuments => Set<FileDocument>();
    public DbSet<DailyReport> DailyReports => Set<DailyReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.UserName).IsUnique();
            e.Property(u => u.Role).HasDefaultValue(Roles.Operator);
            e.Property(u => u.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasOne(t => t.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProductionLine>(e =>
        {
            e.ToTable("line");
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Workstation>(e =>
        {
            e.ToTable("workstation");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => new { x.LineId, x.StepId });
            e.HasOne(x => x.Line).WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Step).WithMany().HasForeignKey(x => x.StepId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProcessStep>(e =>
        {
            e.ToTable("process_step");
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.ToTable("product");
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<ProductRoute>(e =>
        {
            e.ToTable("product_route");
            e.HasIndex(x => new { x.ProductId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.ProductId, x.StepId }).IsUnique();
            e.HasOne(x => x.Product).WithMany(p => p.Routes).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Step).WithMany().HasForeignKey(x => x.StepId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DefectCode>(e =>
        {
            e.ToTable("defect_code");
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<WorkOrder>(e =>
        {
            e.ToTable("work_order");
            e.HasIndex(x => x.OrderNo).IsUnique();
            e.HasIndex(x => new { x.LineId, x.Status });
            e.HasIndex(x => x.DueDateUtc);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Line).WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SerialUnit>(e =>
        {
            e.ToTable("serial_unit");
            e.HasIndex(x => x.Sn).IsUnique();
            e.HasIndex(x => new { x.WorkOrderId, x.Status });
            e.HasIndex(x => new { x.ProductionDate, x.Status });
            e.HasIndex(x => new { x.WorkOrderId, x.CurrentSeq });
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.WorkOrder).WithMany(w => w.Units).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SnMoveLog>(e =>
        {
            e.ToTable("sn_move_log");
            e.HasIndex(x => x.ClientKey).IsUnique();
            e.HasIndex(x => new { x.WorkOrderId, x.StepId, x.Result });
            e.HasIndex(x => new { x.ProductionDate, x.StepId });
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => x.SerialUnitId);
            e.HasOne(x => x.SerialUnit).WithMany().HasForeignKey(x => x.SerialUnitId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Step).WithMany().HasForeignKey(x => x.StepId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DefectCode).WithMany().HasForeignKey(x => x.DefectCodeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RepairRecord>(e =>
        {
            e.ToTable("repair_record");
            // 同一 SN 同一次维修编号唯一：并发双击"进站"时靠它兜底
            e.HasIndex(x => new { x.SerialUnitId, x.RepairNo }).IsUnique();
            e.HasIndex(x => new { x.Status, x.StartedAtUtc });
            e.HasOne(x => x.SerialUnit).WithMany().HasForeignKey(x => x.SerialUnitId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SourceStep).WithMany().HasForeignKey(x => x.SourceStepId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DefectCode).WithMany().HasForeignKey(x => x.DefectCodeId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ExceptionEvent>(e =>
        {
            e.ToTable("exception_event");
            e.HasIndex(x => new { x.LineId, x.Status });
            e.HasIndex(x => x.ReportedAtUtc);
            e.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log");
            // 审计检索的两个主入口：按人查、按时间倒序翻页
            e.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => new { x.Action, x.CreatedAtUtc });
            e.Property(x => x.Success).HasDefaultValue(true);
        });

        modelBuilder.Entity<FileDocument>(e =>
        {
            e.ToTable("file_document");
            // 文件检索的主入口：按类别翻页、按机种找图纸/SOP、按 SN 找检验报告
            e.HasIndex(x => new { x.Category, x.CreatedAtUtc });
            e.HasIndex(x => x.ProductId);
            e.HasIndex(x => x.Sn);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DailyReport>(e =>
        {
            e.ToTable("daily_report");
            // 同一条线同一天只生成一份，重复跑（手动补数）覆盖
            e.HasIndex(x => new { x.ReportDate, x.LineId }).IsUnique();
        });

        // 所有 DateTime 统一按 UTC 存储与读取，保证 JSON 输出带 Z，前端不会按本地时区误解析
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(utcConverter);
                else if (property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(nullableUtcConverter);
            }
        }
    }
}
