using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using MesApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

public interface IMasterDataService
{
    Task<MasterBootstrap> GetBootstrapAsync(CancellationToken ct);
}

/// <summary>
/// 主数据引导包：缓存到 Redis（未配置时自动降级进程内）。
/// 200 台终端开机集中拉取走缓存，不打爆库；将来多实例部署时缓存天然共享。
/// </summary>
public class MasterDataService(MesDbContext db, ICacheService cache) : IMasterDataService
{
    private const string CacheKey = "master:bootstrap";

    public async Task<MasterBootstrap> GetBootstrapAsync(CancellationToken ct)
    {
        var cached = await cache.GetAsync<MasterBootstrap>(CacheKey);
        if (cached is not null) return cached;

        var lines = await db.Lines.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Id)
            .Select(x => new LineDto(x.Id, x.Code, x.Name, x.IsActive))
            .ToListAsync(ct);

        var stations = await db.Workstations.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Id)
            .Select(x => new WorkstationDto(x.Id, x.Code, x.Name, x.LineId, x.StepId, x.Step != null ? x.Step.Name : null))
            .ToListAsync(ct);

        var defects = await db.DefectCodes.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Code)
            .Select(x => new DefectCodeDto(x.Id, x.Code, x.Name, x.Category))
            .ToListAsync(ct);

        var products = await db.Products.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Code)
            .Include(x => x.Routes).ThenInclude(r => r.Step)
            .ToListAsync(ct);

        var productDtos = products.Select(p => new ProductDto(
            p.Id, p.Code, p.Name, p.Category, p.StandardCycleSeconds, p.IsActive,
            p.Routes.OrderBy(r => r.Seq).Select(r => new StepDto(
                r.StepId, r.Step!.Code, r.Step.Name, r.Step.Kind, r.Step.RequireDefect, r.Step.IsKey,
                r.Seq, r.StandardSeconds, r.IsInspection)).ToList()
        )).ToList();

        var steps = productDtos
            .SelectMany(p => p.Route)
            .GroupBy(s => s.Id)
            .Select(g => g.OrderBy(s => s.Seq).First())
            .OrderBy(s => s.Seq)
            .ToList();

        var result = new MasterBootstrap(lines, stations, defects, productDtos, steps);
        await cache.SetAsync(CacheKey, result, TimeSpan.FromMinutes(2));
        return result;
    }
}
