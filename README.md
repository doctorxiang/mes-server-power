# 服务器电源 MES（网页版 · MVP）

面向服务器电源制造的简化 MES，覆盖**插件 → 过锡炉 → 补焊剪脚 → 目视检查 → ICT → 老化 → 耐压 → 功能测试 → 包装**九道工序的扫码过站、不良返修、SN 追溯与车间看板。

技术栈：Vue 3 + TypeScript + Pinia ｜ ASP.NET Core (.NET 10) + EF Core ｜ MySQL 8.4（开发可用 SQLite）｜ Redis / RabbitMQ / MinIO / Quartz.NET / SignalR（均为可选组件，未配置时自动降级）。

## 快速启动

```powershell
.\start-dev.ps1              # SQLite 演示模式，无需 MySQL
.\start-dev.ps1 -WithMysql   # 完整 MySQL（需要 Docker）
```

中间件单独起：

```bash
docker compose up -d         # mysql + redis + rabbitmq + minio + adminer
```

首次部署先把 `.env.example` 复制为 `.env` 并改成自己的密码，`docker compose` 会自动读取。后端连接串密码需与 `.env` 的 `MES_DB_PASSWORD` 一致（改 `appsettings.json`，或用环境变量 `ConnectionStrings__MySql` 覆盖）。

手动启动：

```bash
# 后端（Production 走 MySQL 并执行迁移，Development 走 SQLite）
cd backend/MesApi
dotnet run --urls http://localhost:5080

# 前端
cd frontend && npm install && npm run dev   # http://localhost:5173
```

中间件没配置时后端自动走降级实现（进程内缓存、内存消息、本地磁盘），不影响演示。实际生效情况看 `GET /health/ready` 返回的 `infra` 字段。

注意：SQLite 模式用 `EnsureCreated` 建表，升级后不会给旧 `mes.db` 补新表，需删掉重建；MySQL 模式走正式迁移，无此问题。

## 演示账号

| 账号 | 密码 | 角色 |
|---|---|---|
| admin | Admin@12345 | 系统管理员 |
| planner | Planner@123 | PMC 计划员 |
| leader | Leader@123 | 班组长 |
| operator1 | Operator@123 | 操作工 |
| qc1 | Qc@12345678 | 质检员 |

## 演示数据

首次启动自动初始化主数据、3 张在制工单和约 280 台 SN。需要更丰满的数据时可以补数（15 张工单 / 2368 台 SN / 2.15 万条流水，良品率 98.4%、直通率 84.6%），支持热更新，不用重启：

```bash
dotnet run -- --topup-demo          # 幂等补齐，已有的单号跳过
dotnet run -- --reset-demo          # 清空生产数据后按新口径重建
curl -X POST http://localhost:5080/api/demo/topup -H "Authorization: Bearer <admin token>"
```

安全阀：`Demo:AllowTopUp=false` 或非 Admin 调用时接口直接拒绝。

## 业务口径

工单按「发料 = 计划」执行，不预置冗余料；报废后走补料追加发料。

- `PlanQty` 是计划良品数，完工进度 = 完工 ÷ 计划，到 100% 工单自动 Completed
- 发料是扫码建 SN 的硬上限，用尽后返回 409，须补料后才能继续上线
- 报废即补料：缺口 = 报废数 − (发料 − 计划)，工单详情页提示补料
- 直通率 FPY = 一次未判不良就完工 ÷ 投入，与良品率（完工 ÷ 投入）口径不同，报表中分开列示

维修站：不良 SN 隔离后进站维修，修复出站放回原判不良工序重测；重复维修超 2 次需管理员授权放行，否则只能报废。

QC 抽检针对已包装成品，不合格不报废不隔离，按现场做法加严抽检并回溯同批。

## 功能模块

| 模块 | 说明 |
|---|---|
| 扫码报工 | 扫码枪即插即用（USB 键盘模式）；Enter 合格 / F2 不良；首站扫码自动建 SN |
| 工序流转 | 按工艺路线推进，跳站、混单、隔离品均有拦截 |
| 维修站 | 进出站管理、返修次数计数与超限授权、维修履历进追溯 |
| 工单管理 | 建单下发、进度与补料、关单重开、按产线/日期/关键字查询、工单报告 |
| SN 追溯 | 完整工序履历：每站结果、操作人、工位、时间、不良代码 |
| 车间看板 | 深色大屏，KPI / 小时产出 / WIP / TOP 不良，SignalR 实时推送 |
| 现场异常 | 缺料/设备/品质异常上报与闭环 |
| APS 排程 | 有限产能正排，按瓶颈节拍估算开工完工，甘特图展示 |
| QC 抽检 | 出货前抽样检验台账，含待抽检候选与合格率 |
| 物料补料 | 发料用尽拦截上线，报废一台补一台 |
| 工艺文件 | 图纸 / SOP / 检验报告上传归档（MinIO 或本地磁盘） |
| 任务调度 | Quartz.NET：维修超时、交期预警、夜间报表、数据清理 |
| 账号权限 | 6 种角色，路由与接口双重校验 |
| 操作审计 | 写操作留痕（密码类字段脱敏），管理员可检索 |

## 接口一览

```
POST /api/auth/login|refresh|logout|change-password   认证
GET  /api/master/bootstrap                            主数据引导包
GET  /api/work-orders       POST /api/work-orders     工单列表 / 建单
GET  /api/work-orders/{id}  POST /api/work-orders/{id}/status   详情 / 状态流转
POST /api/production/scan                             扫码过站
GET  /api/production/repair/pending                   待维修队列
POST /api/production/repair/checkin|checkout          维修进出站
GET  /api/qc/inspections    POST /api/qc/inspections  抽检台账 / 录入
GET  /api/trace/{sn}                                  SN 追溯
GET  /api/dashboard?lineId=&date=                     看板聚合
GET  /api/exceptions        POST /api/exceptions      异常上报 / 列表
POST /api/aps/run                                     APS 排程
GET  /api/analysis/yield?by=order|product             工单报告
GET  /api/files             POST /api/files           工艺文件 / 上传
GET  /api/jobs              POST /api/jobs/{name}/run 调度任务 / 手动触发（Admin）
GET  /api/audit                                       操作审计（Admin）
POST /api/work-orders/{id}/replenish                  补料
WS   /hubs/board                                      SignalR 实时推送
GET  /health/live  /health/ready                      探活
GET  /docs                                            接口文档
```

## 测试

```bash
bash backend/test-api.sh              # 接口冒烟 70 项
python scripts/smoke_platform.py      # 平台健壮性 17 项
node .e2e/e2e.mjs                     # 浏览器端到端（另见 .e2e/ 下 repair / features / sidebar / tabs 脚本）
```

## 数据模型（17 张核心表）

```
users / refresh_tokens                  账号与 JWT 刷新令牌（PBKDF2-SHA256）
line / workstation                      产线、工位（每工位绑定一道工序）
process_step / product / product_route  工序字典、机型、工艺路线
defect_code                             不良代码
work_order                              工单（Draft→Released→InProduction→Completed→Closed）
serial_unit                             SN 档案（当前工序、状态、乐观并发版本号）
sn_move_log                             过站流水（只追加，追溯与报表的事实来源）
repair_record                           维修记录（SN+RepairNo 唯一）
qc_inspection                           QC 抽检台账
exception_event                         现场异常
audit_log                               操作审计（默认留存 90 天）
file_document                           文件档案（内容在 MinIO 或本地磁盘）
daily_report                            夜间报表
```

## 部署要点

1. 修改 `appsettings.json` 的 `Jwt:Key`（≥32 字符）与 `Seed:AdminPassword`，登录后改默认密码
2. `docker compose up -d` 起 MySQL，后端以 `ASPNETCORE_ENVIRONMENT=Production` 运行即执行迁移
3. 单端口发布：`npm run build` 后把 `frontend/dist` 拷到 `backend/MesApi/wwwroot/`，由后端托管前端
4. 看板大屏：浏览器打开 `/dashboard` 按 F11 全屏

备份：`python scripts/backup.py --provider mysql --container mes-mysql`，产物在 `backups/`，保留 30 天。

日志在 `backend/MesApi/logs/`，按天滚动保留 30 天；每个请求一行日志，排查问题时按界面提示的请求号 grep 即可。

### 公网演示：GitHub Pages + 本机后端

前端可发布到 GitHub Pages（`.github/workflows/deploy-frontend.yml`，推 main 自动构建），后端仍跑在本机，用内网穿透暴露出去：

```bash
.\scripts\start-tunnel.ps1            # 需要 cloudflared，输出 https://xxx.trycloudflare.com
```

把这个地址填到仓库 Settings → Secrets and variables → Actions → Variables 的 `VITE_API_BASE`，再重跑部署，Pages 站点就会请求本机后端。隧道地址每次重启都会变，换地址后更新 `VITE_API_BASE` 并重跑即可。

## 已知边界（二期建议）

- 物料批次追溯（投料记录、先进先出）未纳入；`sn_move_log` 已预留扩展位
- 老化房设备数采（PLC/OPC UA）未接入，老化结果人工录入
- APS 未含换型时间、班次日历、多产线择优分配
- 多实例部署还差 SignalR Redis backplane 与 Quartz AdoJobStore 集群模式
- 文件上传未做病毒扫描与预览
