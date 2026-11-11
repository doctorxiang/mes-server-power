using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MesApi.Services;

public interface IMasterDataService
{
    Task<MasterBootstrap> GetBootstrapAsync(CancellationToken ct);
}

public class MasterDataService(MesDbContext db, IMemoryCache cache) : IMasterDataService
{
    private const string CacheKey = "master:bootstrap";

    public async Task<MasterBootstrap> GetBootstrapAsync(CancellationToken ct)
    {
        // 主数据变更极少：进程内缓存 2 分钟，供 200 台终端启动时集中拉取
        if (cache.TryGetValue(CacheKey, out MasterBootstrap? cached) && cached is not null)
            return cached;

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
        cache.Set(CacheKey, result, TimeSpan.FromMinutes(2));
        return result;
    }
}
