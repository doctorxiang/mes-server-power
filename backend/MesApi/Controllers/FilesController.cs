using MesApi.Data;
using MesApi.Domain;
using MesApi.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Controllers;

/// <summary>
/// 非结构化文件：图纸、SOP、检验报告、现场照片。
/// 内容进对象存储（MinIO/本地），元数据进库，按机种/工单/SN 都能检索。
/// </summary>
[ApiController]
[Route("api/files")]
[Authorize]
public class FilesController(MesDbContext db, IFileStorage storage, ILogger<FilesController> logger) : ControllerBase
{
    private static readonly string[] AllowedCategories = ["Drawing", "SOP", "InspectionReport", "Photo", "Other"];

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] string? category, [FromQuery] int? productId, [FromQuery] int? workOrderId,
        [FromQuery] string? sn, [FromQuery] int page = 1, [FromQuery] int size = 30, CancellationToken ct = default)
    {
        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);

        var query = db.FileDocuments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(f => f.Category == category);
        if (productId.HasValue) query = query.Where(f => f.ProductId == productId.Value);
        if (workOrderId.HasValue) query = query.Where(f => f.WorkOrderId == workOrderId.Value);
        if (!string.IsNullOrWhiteSpace(sn)) query = query.Where(f => f.Sn == sn);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(f => f.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(f => new
            {
                f.Id,
                f.Category,
                f.ProductId,
                ProductName = f.Product != null ? f.Product.Name : null,
                f.WorkOrderId,
                WorkOrderNo = f.WorkOrder != null ? f.WorkOrder.OrderNo : null,
                f.Sn,
                f.FileName,
                f.SizeBytes,
                f.ContentType,
                f.Remark,
                f.UploadedByName,
                f.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(new { total, page, size, storage = storage.Backend, items });
    }

    [HttpPost]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Leader},{Roles.Qc},{Roles.Planner}")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 20 * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string category,
        [FromForm] int? productId,
        [FromForm] int? workOrderId,
        [FromForm] string? sn,
        [FromForm] string? remark,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { detail = "请选择要上传的文件" });
        if (!AllowedCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { detail = $"分类必须是：{string.Join("/", AllowedCategories)}" });

        var userId = int.TryParse(User.FindFirst("sub")?.Value, out var uid) ? uid : 0;
        var userName = User.FindFirst("name")?.Value ?? "";

        // 对象键带年月与 UUID，防止同名覆盖；保留原始扩展名便于下载时识别
        var ext = Path.GetExtension(file.FileName);
        var objectKey = $"{category}/{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}{ext}";

        await using var stream = file.OpenReadStream();
        try
        {
            await storage.SaveAsync(objectKey, stream, file.ContentType, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "文件上传到对象存储失败");
            return StatusCode(StatusCodes.Status500InternalServerError, new { detail = "文件保存失败，请重试" });
        }

        var doc = new FileDocument
        {
            Category = AllowedCategories.First(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)),
            ProductId = productId,
            WorkOrderId = workOrderId,
            Sn = sn,
            FileName = file.FileName,
            ObjectKey = objectKey,
            SizeBytes = file.Length,
            ContentType = file.ContentType,
            Remark = remark,
            UploadedByUserId = userId,
            UploadedByName = userName
        };
        db.FileDocuments.Add(doc);
        await db.SaveChangesAsync(ct);

        return Ok(new { doc.Id, doc.FileName, doc.Category, storage = storage.Backend });
    }

    [HttpGet("{id:int}/content")]
    public async Task<IActionResult> Download(int id, CancellationToken ct)
    {
        var doc = await db.FileDocuments.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (doc is null) return NotFound(new { detail = "文件不存在" });

        var stream = await storage.OpenAsync(doc.ObjectKey, ct);
        if (stream is null) return NotFound(new { detail = "对象存储中找不到该文件（可能已被清理）" });

        return File(stream, doc.ContentType, doc.FileName);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var doc = await db.FileDocuments.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (doc is null) return NotFound(new { detail = "文件不存在" });

        try { await storage.DeleteAsync(doc.ObjectKey, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "删除对象存储文件失败 key={Key}", doc.ObjectKey); }

        db.FileDocuments.Remove(doc);
        await db.SaveChangesAsync(ct);
        return Ok(new { message = "已删除" });
    }
}
