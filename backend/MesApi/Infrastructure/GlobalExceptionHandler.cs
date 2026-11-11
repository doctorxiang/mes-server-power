using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MesApi.Infrastructure;

/// <summary>全局异常处理：业务异常 -> 4xx，其它 -> 500，统一返回 ProblemDetails（前端可稳定解析 detail/title）。</summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is AppException appEx)
        {
            httpContext.Response.StatusCode = appEx.StatusCode;
            await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = appEx.StatusCode,
                Title = appEx.StatusCode == StatusCodes.Status401Unauthorized ? "Unauthorized" : "Request failed",
                Detail = appEx.Message,
                Instance = httpContext.Request.Path
            }, cancellationToken);
            return true;
        }

        logger.LogError(exception, "未处理异常：{Path}", httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "服务器内部错误",
            Detail = env.IsDevelopment() ? exception.ToString() : "服务器开小差了，请稍后重试",
            Instance = httpContext.Request.Path
        }, cancellationToken);
        return true;
    }
}
