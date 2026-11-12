using System.Diagnostics;
using Serilog.Context;

namespace MesApi.Infrastructure;

/// <summary>
/// 请求上下文：绑定链路 Id、记录耗时、慢请求告警。
/// 前端报"刚才那单没成功"时，凭响应头 X-Request-Id 就能在日志/审计表里精确捞到那一次请求。
/// </summary>
public class RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> logger, IConfiguration config)
{
    private readonly int _slowMs = config.GetValue("Diagnostics:SlowRequestMs", 800);

    public async Task InvokeAsync(HttpContext context)
    {
        // 优先沿用调用方（网关/前端）传入的 Id，方便全链路对齐
        var requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64)
            requestId = Guid.NewGuid().ToString("N");

        context.TraceIdentifier = requestId;
        context.Items["RequestId"] = requestId;

        // 用 OnStarting 而不是直接赋值：异常处理中间件会 Clear 响应（含响应头），
        // 只有响应真正开始写之前追加才不会被清掉。
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Request-Id"] = requestId;
            return Task.CompletedTask;
        });

        var sw = Stopwatch.StartNew();
        using (LogContext.PushProperty("RequestId", requestId))
        using (LogContext.PushProperty("ClientIp", context.Connection.RemoteIpAddress?.ToString()))
        {
            try
            {
                await next(context);
            }
            finally
            {
                sw.Stop();
                context.Items["ElapsedMs"] = (int)sw.ElapsedMilliseconds;

                // 健康检查与静态资源不刷日志，避免 200 终端轮询淹没有效信息
                var path = context.Request.Path.Value ?? string.Empty;
                var noisy = path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                            || path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)
                            || path.StartsWith("/assets", StringComparison.OrdinalIgnoreCase);

                if (!noisy && sw.ElapsedMilliseconds >= _slowMs)
                {
                    logger.LogWarning("慢请求 {Method} {Path} {Status} 耗时 {Elapsed}ms",
                        context.Request.Method, path, context.Response.StatusCode, sw.ElapsedMilliseconds);
                }
            }
        }
    }
}

public static class RequestContextMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestContext(this IApplicationBuilder app)
        => app.UseMiddleware<RequestContextMiddleware>();
}
