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
WS   /hubs/board                                      SignalR 实时推送（scan 事件）
```

调试文件：`backend/MesApi.http`；接口冒烟脚本：`bash backend/test-api.sh`（52 项断言）；
浏览器端到端：`node .e2e/e2e.mjs`（18 项）+ `node .e2e/repair-e2e.mjs`（维修站 7 项，含截图）。

## 数据模型（11 张核心表）

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

## 已知边界（二期建议）

- 物料批次追溯（投料记录、先进先出）未纳入 MVP；`sn_move_log` 已预留扩展位
- 老化房设备数采（PLC/OPC UA）未接入，老化结果目前由人工判定录入
- 排产为优先级+交期简单排序，未做有限产能排程
- 多实例部署需引入 Redis（SignalR backplane + 主数据缓存失效同步）
