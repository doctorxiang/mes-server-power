"""平台健壮性冒烟脚本：健康检查 / 审计 / 链路 Id / 404 语义。
用法：python scripts/smoke_platform.py [baseUrl]
"""
import json
import sys
import urllib.error
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5080"
passed = 0
failed = []


def call(method, path, body=None, token=None, raw=False):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data:
        req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            text = r.read().decode("utf-8", "replace")
            return r.status, dict(r.headers), (text if raw else json.loads(text or "{}"))
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", "replace")
        try:
            payload = text if raw else json.loads(text or "{}")
        except json.JSONDecodeError:
            payload = text
        return e.code, dict(e.headers), payload


def check(name, cond, detail=""):
    global passed
    if cond:
        passed += 1
        print(f"  OK   {name}")
    else:
        failed.append(name)
        print(f"  FAIL {name} {detail}")


print("== 健康检查 ==")
s, _, d = call("GET", "/health/live")
check("live 200", s == 200, s)
check("live healthy", d.get("status") == "healthy", d)

s, _, d = call("GET", "/health/ready")
check("ready 200", s == 200, s)
check("ready 无待应用迁移", d.get("pendingMigrations") == 0, d.get("pendingMigrations"))
check("ready 数据库 Healthy", (d.get("checks") or [{}])[0].get("status") == "Healthy", d.get("checks"))

print("== SPA 与 API 路由语义 ==")
s, h, d = call("GET", "/docs", raw=True)
check("docs 200 html", s == 200 and "html" in h.get("Content-Type", ""), s)
s, h, d = call("GET", "/api/not-exist")
check("未知 /api 返回 404 JSON", s == 404 and "json" in h.get("Content-Type", ""), f"{s} {h.get('Content-Type')}")

print("== 链路追踪 Id ==")
s, h, _ = call("POST", "/api/auth/login", {"userNameOrEmail": "admin", "password": "wrong-pass-1"})
check("401 响应带 X-Request-Id", bool(h.get("X-Request-Id")), h.get("X-Request-Id"))
check("错误密码 401", s == 401, s)

print("== 审计日志 ==")
s, _, d = call("POST", "/api/auth/login", {"userNameOrEmail": "admin", "password": "Admin@12345"})
check("管理员登录成功", s == 200, s)
token = d.get("accessToken", "")

s, _, d = call("GET", "/api/audit?action=Auth.Login&size=5", token=token)
check("审计可按动作查询", s == 200 and d.get("total", 0) >= 1, f"{s} {d}")
rows = d.get("items", []) if s == 200 else []
login_rows = [r for r in rows if r.get("action") == "Auth.Login"]
check("登录被审计", len(login_rows) >= 1)
if login_rows:
    check("失败标记正确", any(r["success"] is False and r["statusCode"] == 401 for r in login_rows))
    detail_id = login_rows[0]["id"]
    s2, _, full = call("GET", f"/api/audit/{detail_id}", token=token)
    payload = (full or {}).get("payload") or ""
    check("密码已脱敏", "wrong-pass-1" not in payload and "Admin@12345" not in payload, payload)
    check("保留了操作人", (full or {}).get("userName") is not None, full)

s, _, _ = call("GET", "/api/audit", token=None)
check("未登录查审计 401", s == 401, s)

s, _, d = call("POST", "/api/auth/login", {"userNameOrEmail": "operator1", "password": "Operator@123"})
op_token = d.get("accessToken", "") if s == 200 else ""
if op_token:
    s, _, _ = call("GET", "/api/audit", token=op_token)
    check("操作工查审计 403", s == 403, s)
else:
    print("  SKIP 操作工账号不可用")

print(f"\n结果：{passed} 通过 / {len(failed)} 失败")
if failed:
    print("失败项：" + ", ".join(failed))
    sys.exit(1)
