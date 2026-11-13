using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MesApi.Data;
using MesApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Infrastructure;

/// <summary>
/// 审计中间件：对所有写操作（POST/PUT/DELETE/PATCH）落一条 audit_log。
/// 高频接口（默认 /api/production/scan）在排除名单中，其动作已记录在 sn_move_log。
/// 关键点：审计写在 finally 里，业务抛异常（4xx/5xx）时同样要留痕。
/// </summary>
public partial class AuditMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<AuditMiddleware> logger)
{
    private readonly bool _enabled = config.GetValue("Audit:Enabled", true);
    private readonly string[] _exclude = config.GetSection("Audit:ExcludePaths").Get<string[]>() ?? [];
    private readonly int _maxPayload = config.GetValue("Audit:MaxPayloadChars", 1000);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled || !ShouldAudit(context, out var path))
        {
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        Exception? error = null;

        // 允许后续读取请求体，同时保证控制器还能读到
        context.Request.EnableBuffering();
        var payload = await ReadBodyAsync(context.Request);

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // 异常还会继续往上传给 UseExceptionHandler，这里只负责把状态码与原因带进审计
            error = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            var status = error switch
            {
                AppException appEx => appEx.StatusCode,
                not null => StatusCodes.Status500InternalServerError,
                _ => context.Response.StatusCode
            };
            await SafeWriteAsync(context, path, payload, (int)sw.ElapsedMilliseconds, status, error?.Message);
        }
    }

    private async Task SafeWriteAsync(HttpContext context, string path, string? payload, int elapsedMs, int statusCode, string? errorMessage)
    {
        try
        {
            var userName = context.User.FindFirst("name")?.Value ?? context.User.Identity?.Name;
            // 登录接口在认证之前，此时没有身份，从请求体里取用户名（谁在尝试登录必须可查）
            userName ??= ExtractLoginName(payload);

            var entry = new AuditLog
            {
                UserId = int.TryParse(context.User.FindFirst("sub")?.Value, out var uid) ? uid : null,
                UserName = Truncate(userName, 64),
                Role = context.User.FindFirst("role")?.Value,
                Method = context.Request.Method,
                Path = path,
                Action = BuildAction(context, path),
                StatusCode = statusCode,
                Success = statusCode < 400,
                Payload = payload,
                Message = Truncate(errorMessage ?? context.Items["ErrorDetail"] as string, 512),
                ClientIp = context.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 256),
                RequestId = context.TraceIdentifier,
                DurationMs = elapsedMs
            };

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 审计失败绝不能影响业务主流程，只记日志
            logger.LogError(ex, "审计日志写入失败：{Path}", path);
        }
    }

    private bool ShouldAudit(HttpContext context, out string path)
    {
        path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return false;

        var method = context.Request.Method;
        if (method is not ("POST" or "PUT" or "DELETE" or "PATCH")) return false;

        foreach (var ex in _exclude)
        {
            if (!string.IsNullOrWhiteSpace(ex) && path.StartsWith(ex, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private async Task<string?> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is 0 or null) return null;

        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body)) return null;
        body = SecretRegex().Replace(body, "\"$1\":\"***\"");
        return Truncate(body, _maxPayload);
    }

    private static string? ExtractLoginName(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        var m = LoginNameRegex().Match(payload);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Production.Scan 这种形式：控制器 + Action，比裸 URL 更适合按动作筛选。</summary>
    private static string? BuildAction(HttpContext context, string path)
    {
        var route = context.GetRouteData();
        var controller = route.Values["controller"]?.ToString();
        var action = route.Values["action"]?.ToString();
        if (!string.IsNullOrEmpty(controller) && !string.IsNullOrEmpty(action))
            return $"{controller}.{action}";

        // 未匹配到端点（404）时退化成路径
        return path.TrimStart('/').Replace('/', '.');
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max] + "…";

    [GeneratedRegex("\"(password|newPassword|currentPassword|refreshToken|token|secret)\"\\s*:\\s*\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRegex();

    [GeneratedRegex("\"userNameOrEmail\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LoginNameRegex();
}

public static class AuditMiddlewareExtensions
{
    public static IApplicationBuilder UseAudit(this IApplicationBuilder app) => app.UseMiddleware<AuditMiddleware>();
}
