# 服务器电源 MES（网页版 · MVP）

面向**服务器电源制造**的简化 MES：从**插件 → 过锡炉 → 补焊剪脚 → 目视检查 → ICT → 老化 → 耐压 → 功能测试 → 包装**九道工序的全流程扫码过站、不良隔离与返修、SN 级追溯、实时车间看板。

技术栈：**Vue 3 + TypeScript + Vite + Pinia** ／ **ASP.NET Core (.NET 10) + EF Core 9** ／ **MySQL 8.4**（开发可用 SQLite 快速体验）／ **SignalR** 看板实时推送。

---

## 快速启动

```powershell
# 方式一：SQLite 演示模式（无需安装 MySQL，适合体验）
.\start-dev.ps1

# 方式二：完整 MySQL（需要 Docker）
.\start-dev.ps1 -WithMysql
```

手动启动：

```bash
# 后端（默认 MySQL；本机无 MySQL 时用 SQLite 演示）
cd backend/MesApi
ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://localhost:5080   # SQLite
ASPNETCORE_ENVIRONMENT=Production  dotnet run --urls http://localhost:5080   # MySQL（先 docker compose up -d）

# 前端
cd frontend && npm install && npm run dev   # → http://localhost:5173
```

> SQLite 演示模式用 `EnsureCreated` 建表，**不会给已有的 mes.db 补新表**。从旧版本升级时先删掉 `backend/MesApi/mes.db`（演示数据会重新初始化）；MySQL 模式走正式迁移，无需手工干预。

**运维接口**：`/health/live`、`/health/ready`（探活）、`/docs`（内网接口文档）、`/audit`（界面里的操作审计，仅管理员）。

**演示账号**（首次启动自动初始化主数据与演示生产数据）：

| 账号 | 密码 | 角色 |
|---|---|---|
| admin | Admin@12345 | 系统管理员 |
| planner | Planner@123 | PMC 计划员 |
| leader | Leader@123 | 班组长 |
| operator1 | Operator@123 | 操作工 |
| qc1 | Qc@12345678 | 质检员 |

## 功能清单

| 模块 | 说明 |
|---|---|
| 扫码报工 | 扫码枪键盘输入回车即提交；首站扫码自动建 SN；工位记忆在本地；Enter 合格 / F2 不良；蜂鸣提示 |
| 工序流转 | 严格按工艺路线推进：**跳站拦截、混单拦截、隔离品拦截**；完工自动判定工单完成 |
| 不良处理 | 不良必选不良代码（17 个内置：虚焊/连锡/锡珠/极性反/耐压不良/老化掉电…）；隔离后进**维修站**闭环 |
| 维修站 | 不良 SN 进出站管理：进站确认维修（措施/不良代码/备注）→ 修复出站**放回原判不良工序重测**；重复维修自动计数，**超过 2 次需管理员授权**否则只能报废；维修履历进追溯时间线 |
| 工单管理 | 建单（建单即下发）、进度条、9 道工序 WIP 分布、SN 列表、关单/重开 |
| SN 追溯 | 完整工序履历：每站结果、操作人、工位、时间、循环时长、不良代码 |
| 车间看板 | 深色大屏：6 项 KPI、小时产出、WIP 分布、TOP 不良、产线对比、实时过站流、现场异常 |
| 实时推送 | SignalR `/hubs/board`，扫码事件毫秒级推到看板；断线自动重连 + 8 秒轮询兜底 |
| 现场异常 | 缺料/设备/品质异常上报与闭环处理 |
| 账号权限 | 6 种角色（管理员/计划员/班组长/操作工/质检/只读），路由与接口双重校验 |
| 操作审计 | 所有写操作留痕（谁/何时/哪台终端/成功与否/失败原因），密码类字段自动脱敏，管理员可按人、动作、时间检索 |
| 可观测性 | `/health/live` 与 `/health/ready` 探活；每请求一行结构化日志 + 慢请求告警；`X-Request-Id` 全链路串联 |

## 为 200 终端并发做的设计

- **幂等过站**：每笔扫码带 `clientKey`（终端 UUID），唯一索引兜底，断网重试不重复计数
- **乐观并发**：SN 行版本号，两工位同时扫同一 SN 只有一个成功，返回明确提示
- **索引化聚合**：看板全部按 `production_date`（东八区生产日期字符串）+ 步骤分组，免时区函数、免全表扫描
- **主数据缓存**：工艺路线/工位进程内缓存 2 分钟，终端启动集中拉取不打爆库
- **限流**：登录按 IP 20 次/分；扫码按用户 600 次/分（约每终端 3 秒一笔的余量）
- MySQL 连接 `EnableRetryOnFailure`；估算单实例可承载 200 终端（~10 QPS 过站），更大规模加 Redis 缓存 + SignalR Redis backplane 即可横向扩展

## 接口一览

```
POST /api/auth/login|refresh|logout|change-password   认证（工号/用户名/邮箱均可登录）
GET  /api/auth/users        POST /api/auth/users      账号管理（Admin）
GET  /api/master/bootstrap                            主数据引导包
GET  /api/work-orders       POST /api/work-orders     工单列表 / 建单
GET  /api/work-orders/{id}  POST /api/work-orders/{id}/status   详情 / release|close|reopen
POST /api/production/scan                             扫码过站（核心）
GET  /api/production/recent                           工位最近过站流水
POST /api/production/hold-handle?sn=                  不良返修/报废（Leader/Qc，兼容旧入口）
GET  /api/production/repair/pending                   维修站待维修队列（Leader/Qc）
POST /api/production/repair/checkin                   维修站进站（重复维修超上限需 force=true 管理员授权）
POST /api/production/repair/checkout                  维修站出站（repaired=放回原工序重测 / scrap=报废）
GET  /api/trace/{sn}                                  SN 追溯（含维修记录时间线）
GET  /api/dashboard?lineId=&date=                     看板聚合
GET  /api/exceptions        POST /api/exceptions      异常列表 / 上报
POST /api/exceptions/{id}/close                       异常闭环
GET  /api/audit?from=&to=&userName=&action=&success=  操作审计（Admin，分页）
GET  /api/audit/{id}                                  审计详情（含请求体与请求号）
WS   /hubs/board                                      SignalR 实时推送（scan / alert 事件）
GET  /health/live  /health/ready                      探活（进程存活 / 依赖可用，含磁盘与待应用迁移）
GET  /docs                                            内网接口文档（无外网依赖）
```

调试文件：`backend/MesApi.http`；接口冒烟脚本：`bash backend/test-api.sh`（52 项断言）；
平台健壮性冒烟：`python scripts/smoke_platform.py`（17 项：健康检查/审计/链路 Id/404 语义）；
浏览器端到端：`node .e2e/e2e.mjs`（18 项）+ `node .e2e/repair-e2e.mjs`（维修站 7 项，含截图）。

## 数据模型（12 张核心表）

```
users / refresh_tokens                  账号与 JWT 刷新令牌（PBKDF2-SHA256 21 万次迭代）
line / workstation                      产线、工位（每工位绑定一道工序）
process_step / product / product_route  工序字典、机型、工艺路线（序号+标准工时）
defect_code                             不良代码（外观/焊接/电气/包装/来料）
work_order                              工单（状态机 Draft→Released→InProduction→Completed→Closed）
serial_unit                             SN 档案（当前工序、状态：在制/隔离/完工/报废，乐观并发版本号）
sn_move_log                             过站流水（只追加；追溯与报表唯一事实来源；含幂等键/生产日期/小时/班次）
repair_record                           维修记录（第 N 次维修、不良工序/代码、措施、进出站人与时间；SN+RepairNo 唯一）
exception_event                         现场异常
audit_log                               操作审计（脱敏请求体、状态码、耗时、终端 IP、请求号；默认留存 90 天）
```

### 维修站进出站规则

```
判不良(Fail) → SN 隔离(Hold) ──> 维修站进站（repairNo = 历史次数 + 1）
                                   │  重复进站拦截（已有 InProgress 记录 → 409）
                                   │  超上限拦截（已维修 ≥2 次且非管理员 → 409，管理员可 force）
                                   ▼
                        修复出站(repaired) ──> SN 回到「原判不良工序」重新扫码重测
                        报废出站(scrap)   ──> SN 终态 Scrapped，写 Scrap 过站流水
重测再不良 → 再次隔离 → 再次进站（repairNo 递增，即"重复维修"闭环）
```

## 部署要点（上产线前）

1. **改密**：`appsettings.json` 的 `Jwt:Key`（≥32 字符）与 `Seed:AdminPassword`，登录后立即改默认密码
2. **数据库**：`docker compose up -d` 起 MySQL 8.4，后端以 `ASPNETCORE_ENVIRONMENT=Production` 运行即走正式迁移
3. **单端口发布**：`frontend && npm run build`，把 `dist/*` 拷到 `backend/MesApi/wwwroot/`，后端即托管前端（车间只需访问一个地址）
4. **扫码枪**：即插即用（USB HID 键盘模式），无需驱动；PDA/手机浏览器同样可用
5. **看板大屏**：浏览器打开 `/dashboard` 按 F11 全屏

## 运维手册（上线后每天要用）

### 日志

- 位置：`backend/MesApi/logs/mes-YYYYMMDD.log`（按天滚动，单文件 50MB 溢出切分，保留 30 天）
- 每个请求一行：`HTTP POST /api/production/scan -> 200 in 12.4ms`，慢请求自动升级为 `WRN`（阈值 `Diagnostics:SlowRequestMs`，默认 800ms）
- 用户报"某单没成功"时，让他把界面提示里的**请求号**发过来，按 `grep <请求号> logs/*.log` 精确定位；或直接在「操作审计」页搜该请求号

### 探活（给负载均衡 / 监控用）

```bash
curl http://<host>:5080/health/live    # 进程活着就 200
curl http://<host>:5080/health/ready   # DB 可连 + 迁移已应用 + 磁盘空间，不可用返回 503
```

两者分开：数据库抖动时只摘流量，不重启容器（否则冷启动反而更慢）。

### 操作审计

- 入口：登录后「系统 → 操作审计」（仅管理员）
- 记录范围：所有 `POST/PUT/DELETE/PATCH`，**过站扫码除外**（`Audit:ExcludePaths`）——扫码本身已在 `sn_move_log` 里全量留痕，重复记一份会让高峰写入翻倍
- 已自动脱敏：`password / newPassword / refreshToken / token / secret`
- 留存：默认 90 天（`Audit:RetentionDays`），后台任务每天清理；`sn_move_log` 永不清理（追溯与审厂的原始凭证）

### 备份与恢复

```bash
# MySQL（走 docker 容器内的 mysqldump，现场机器不用装客户端）
MES_DB_PASSWORD=*** python scripts/backup.py --provider mysql --container mes-mysql

# SQLite 演示模式（用在线备份 API，WAL 未落盘的数据也不会丢）
python scripts/backup.py --provider sqlite
```

- 产物在 `backups/`，默认保留 30 天，只清理脚本自己产出的 `mes_*` 文件
- Windows 服务器也可用 `scripts/backup.ps1`（需 `-ExecutionPolicy Bypass` 或进程级放行）
- 恢复 MySQL：`gunzip -c backups/mes_mysql_*.sql.gz | docker exec -i mes-mysql mysql -u root -p mes_server_power`
- 建议挂 Windows 任务计划：每天 02:00 跑一次，并把 `backups/` 同步到另一台机器/网盘

### 现场排障对照表

| 现象 | 先看什么 |
|---|---|
| 扫码没反应 / 提示无法连接 | `/health/ready`；浏览器 F12 Network 看请求是否 4xx/5xx 与请求号 |
| 提示"请求过于频繁"（429） | 该账号 1 分钟内扫码超 600 次，多为扫码枪连发或多人共用账号，按工位拆账号 |
| 提示"已在该工序/工序不匹配" | 正常防呆：SN 当前工序与扫描工位不一致，查 `/trace/{sn}` 看它停在哪 |
| 数据不对但接口成功 | 「操作审计」按操作人 + 时间查，确认是谁在哪台终端改的 |
| 后台任务没生效 | 日志搜"维护任务执行失败"，多为数据库连接抖动，下个周期会重试 |

## 已知边界（二期建议）

- 物料批次追溯（投料记录、先进先出）未纳入 MVP；`sn_move_log` 已预留扩展位
- 老化房设备数采（PLC/OPC UA）未接入，老化结果目前由人工判定录入
- 排产为优先级+交期简单排序，未做有限产能排程
- 多实例部署需引入 Redis（SignalR backplane + 主数据缓存失效同步）
