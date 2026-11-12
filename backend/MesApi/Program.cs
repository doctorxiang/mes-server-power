using System.Text;
using System.Threading.RateLimiting;
using MesApi.Data;
using MesApi.Hubs;
using MesApi.Infrastructure;
using MesApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// ---------- 日志：Serilog（控制台 + 按天滚动文件，保留 30 天）----------
// 车间现场排障靠的就是这份文件日志：设备不联网、运维登不上服务器时，直接把 logs 目录拷走。
var logDirectory = builder.Configuration["Logging:LogDirectory"] ?? "logs";
var logLevel = Enum.TryParse<LogEventLevel>(builder.Configuration["Logging:MinimumLevel:Default"], true, out var lv)
    ? lv
    : LogEventLevel.Information;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {RequestId}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logDirectory, "mes-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {RequestId} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

try
{
    // ---------- 配置 ----------
    var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
    builder.Services.Configure<JwtOptions>(jwtSection);
    var jwt = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

    if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
        throw new InvalidOperationException("Jwt:Key 未配置或长度不足 32 个字符，请检查 appsettings 或环境变量 Jwt__Key。");

    // ---------- 数据库：MySql（默认）/ Sqlite（本地快速验证）----------
    var provider = builder.Configuration["Database:Provider"] ?? "MySql";
    builder.Services.AddDbContext<MesDbContext>(options =>
    {
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // Sqlite 仅用于本地快速验证：迁移按 MySQL 方言生成，忽略 provider 差异导致的模型校验警告
            options.UseSqlite(builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=mes.db")
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        }
        else
        {
            var cs = builder.Configuration.GetConnectionString("MySql")
                     ?? throw new InvalidOperationException("缺少连接字符串 ConnectionStrings:MySql");
            // 固定版本，避免启动探测；EnableRetryOnFailure 应对车间网络抖动
            options.UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36)),
                my => my.EnableRetryOnFailure(3, TimeSpan.FromSeconds(3), null));
        }
    });

    builder.Services.AddMemoryCache();

    // 停机时给在途扫码请求留出完成时间，避免正在写库的请求被硬切
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

    // 车间接口请求体都很小（扫码报文不到 1KB），限制 1MB 可挡掉异常大包
    builder.Services.Configure<KestrelServerOptions>(o =>
    {
        o.Limits.MaxRequestBodySize = 1024 * 1024;
        o.AddServerHeader = false; // 不暴露服务器指纹
    });
    builder.Services.AddScoped<ITokenService, TokenService>();
    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<IProductionService, ProductionService>();
    builder.Services.AddScoped<IWorkOrderService, WorkOrderService>();
    builder.Services.AddScoped<IDashboardService, DashboardService>();
    builder.Services.AddScoped<ITraceService, TraceService>();
    builder.Services.AddScoped<IMasterDataService, MasterDataService>();
    builder.Services.AddScoped<IExceptionService, ExceptionService>();
    builder.Services.AddSingleton<IBoardNotifier, BoardNotifier>();

    // 后台维护任务：维修超时预警 / 令牌清理 / 审计日志留存
    builder.Services.AddHostedService<MaintenanceWorker>();

    // 健康检查：/health/live 与 /health/ready
    builder.Services.AddMesHealthChecks();

    // ---------- 认证 / 授权 ----------
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.MapInboundClaims = false; // 保持短 claim 名，避免 role/sub 被重映射
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "name",
                RoleClaimType = "role"
            };
            // SignalR 通过 query string 传 token（浏览器 WebSocket 无法自定义 Header）
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        context.Token = accessToken;
                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();
    builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 32 * 1024);

    // ---------- CORS ----------
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                         ?? ["http://localhost:5173", "http://127.0.0.1:5173"];
    builder.Services.AddCors(options =>
    {
    options.AddPolicy("frontend", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        // 让浏览器能读到链路 Id，前端报错时才能把它显示给用户
        .WithExposedHeaders("X-Request-Id"));
    });

    // ---------- 限流 ----------
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // 登录防爆破：按 IP 每分钟 20 次
        options.AddPolicy("auth", httpContext =>
        {
            var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"auth:{key}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        });

        // 扫码：按登录用户限流。200 台终端大多是操作工，单人多工位共用账号也不至于超限
        options.AddPolicy("scan", httpContext =>
        {
            var key = httpContext.User.FindFirst("sub")?.Value
                      ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"scan:{key}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        });

        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.ContentType = "application/problem+json";
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                status = StatusCodes.Status429TooManyRequests,
                title = "请求过于频繁",
                detail = "操作太频繁，请稍后再试"
            }, ct);
        };
    });

    // ---------- 统一错误处理 / 控制器 / OpenAPI ----------
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddControllers();
    builder.Services.AddOpenApi();

    var app = builder.Build();

    // 反向代理（Nginx / IIS）后取真实客户端 IP
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    app.UseExceptionHandler();
    app.UseRequestContext();

    // 每个请求一行结构化日志，超过阈值的慢请求自动升级为 Warning
    var slowMs = app.Configuration.GetValue("Diagnostics:SlowRequestMs", 800);
    app.UseSerilogRequestLogging(opts =>
    {
        opts.MessageTemplate = "HTTP {RequestMethod} {RequestPath} -> {StatusCode} in {Elapsed:0.0}ms";
        opts.GetLevel = (ctx, elapsed, ex) =>
            ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
            : elapsed > slowMs ? LogEventLevel.Warning
            : ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/assets")
                ? LogEventLevel.Debug
                : LogEventLevel.Information;
    });

    app.UseCors("frontend");
    app.UseAuthentication();
    app.UseAudit();
    app.UseRateLimiter();
    app.UseAuthorization();

    if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Docs:Enabled", true))
    {
        app.MapOpenApi();
        app.MapDocsPage();
    }

    app.MapControllers();
    app.MapHub<BoardHub>("/hubs/board");
    app.MapMesHealthChecks();

    // ---------- 单端口部署：前端构建产物放 wwwroot 时由后端直接托管 ----------
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // 不存在的 /api 返回 JSON 404，而不是落到前端 index.html
    // （否则前端拿到 HTML 却按 JSON 解析，报出与真实原因无关的怪错）
    app.Map("/api/{**rest}", () => Results.Json(new
    {
        status = StatusCodes.Status404NotFound,
        title = "接口不存在",
        detail = "请检查请求路径与方法"
    }, statusCode: StatusCodes.Status404NotFound));

    app.MapFallbackToFile("index.html");

    // ---------- 启动时建表 + 种子数据 ----------
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        // Sqlite 走 EnsureCreated（迁移脚本是 MySQL 方言，自增列不通用）；MySQL 走正式迁移
        if (db.Database.IsSqlite())
            await db.Database.EnsureCreatedAsync();
        else
            await db.Database.MigrateAsync();

        await SeedData.EnsureSeededAsync(db, app.Configuration, logger);
    }

    Log.Information("MES 后端启动完成，数据库提供者={Provider}，环境={Env}", provider, app.Environment.EnvironmentName);
    app.Run();
}
catch (HostAbortedException)
{
    // dotnet-ef 设计时构建宿主后会主动中止，属正常流程，不当作启动失败
    throw;
}
catch (Exception ex)
{
    Log.Fatal(ex, "MES 后端启动失败");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
