#!/usr/bin/env bash
# 服务器电源 MES 接口冒烟测试：认证 -> 建单 -> 扫码过站 -> 幂等 -> 跳站拦截 -> 不良隔离 -> 维修站进出站 -> 重复维修上限 -> 追溯 -> 看板 -> 权限
set -u
BASE=${BASE:-http://localhost:5080}
TOKEN=""

api() { curl -s --noproxy '*' -H "Content-Type: application/json" ${TOKEN:+-H "Authorization: Bearer $TOKEN"} "$@"; }
code() { curl -s --noproxy '*' -o /dev/null -w "%{http_code}" -H "Content-Type: application/json" ${TOKEN:+-H "Authorization: Bearer $TOKEN"} "$@"; }
# python 取值：jget '<python 表达式，d 为响应>'  <<< json
jget() { python -c "import sys,json;d=json.load(sys.stdin);print($1)" 2>/dev/null; }

pass=0; fail=0
check() {
  if [ "$2" = "$3" ]; then echo "  ✓ $1（$3）"; pass=$((pass+1));
  else echo "  ✗ $1：期望 $2，实际 $3"; fail=$((fail+1)); fi
}

echo "=== 1. 认证与权限边界 ==="
LOGIN=$(api -X POST $BASE/api/auth/login -d '{"userNameOrEmail":"admin","password":"Admin@12345"}')
TOKEN=$(echo "$LOGIN" | jget "d['accessToken']")
check "管理员登录" "true" "$([ -n "$TOKEN" ] && echo true || echo false)"

TOKEN=""
check "错误密码拦截" "401" "$(code -X POST $BASE/api/auth/login -d '{"userNameOrEmail":"admin","password":"wrongpass1"}')"
check "无令牌访问工单" "401" "$(code $BASE/api/work-orders)"
check "无令牌访问看板" "401" "$(code $BASE/api/dashboard)"

TOKEN=$(echo "$LOGIN" | jget "d['accessToken']")

echo "=== 2. 主数据与建单 ==="
BOOT=$(api $BASE/api/master/bootstrap)
check "主数据：工序 9 道" "9" "$(echo "$BOOT" | jget "len(d['steps'])")"
check "主数据：产品 3 个" "3" "$(echo "$BOOT" | jget "len(d['products'])")"
check "主数据：工位 20 个（含 2 个维修站）" "20" "$(echo "$BOOT" | jget "len(d['workstations'])")"

CREATED=$(api -X POST $BASE/api/work-orders -d '{"productId":1,"lineId":1,"planQty":20,"priority":2,"plannedStartUtc":"2026-11-10T08:00:00Z","dueDateUtc":"2026-11-12T08:00:00Z","remark":"冒烟测试单"}')
ORDER=$(echo "$CREATED" | jget "d['summary']['orderNo']")
check "创建工单成功" "true" "$([ -n "$ORDER" ] && echo true || echo false)"
echo "  工单号：$ORDER"

echo "=== 3. 首站上线与工序推进 ==="
SN="T$(date +%H%M%S)01"
R1=$(api -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-01\",\"stepCode\":\"PLUGIN\",\"result\":\"Pass\",\"clientKey\":\"k-1-$SN\"}")
check "插件站上线" "True" "$(echo "$R1" | jget "d['accepted']")"
check "自动流转至过锡炉" "过锡炉" "$(echo "$R1" | jget "d['nextStepName']")"

echo "=== 4. 幂等：同 clientKey 重复提交 ==="
R2=$(api -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-01\",\"stepCode\":\"PLUGIN\",\"result\":\"Pass\",\"clientKey\":\"k-1-$SN\"}")
check "重复提交被识别" "True" "$(echo "$R2" | jget "d['duplicated']")"

echo "=== 5. 跳站拦截 ==="
check "跳过 3 站直接 ICT -> 409" "409" "$(code -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-05\",\"stepCode\":\"ICT\",\"result\":\"Pass\",\"clientKey\":\"k-skip-$SN\"}")"

echo "=== 6. 不良隔离 ==="
R3=$(api -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-02\",\"stepCode\":\"WAVE\",\"result\":\"Pass\",\"clientKey\":\"k-2-$SN\"}")
check "过锡炉通过" "InProcess" "$(echo "$R3" | jget "d['unitStatus']")"
R3b=$(api -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-03\",\"stepCode\":\"TOUCHUP\",\"result\":\"Pass\",\"clientKey\":\"k-3-$SN\"}")
check "补焊剪脚通过" "目视检查" "$(echo "$R3b" | jget "d['nextStepName']")"

R4=$(api -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-04\",\"stepCode\":\"VISUAL\",\"result\":\"Fail\",\"defectCode\":\"WELD-01\",\"remark\":\"冒烟测试虚焊\",\"clientKey\":\"k-4-$SN\"}")
check "目视不良 -> 隔离" "Hold" "$(echo "$R4" | jget "d['unitStatus']")"
SN2="N$(date +%H%M%S)01"
check "首站禁止判不良/缺代码" "400" "$(code -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN2\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-01\",\"stepCode\":\"PLUGIN\",\"result\":\"Fail\",\"clientKey\":\"k-nocode-$SN\"}")"
check "隔离品禁止继续过站" "409" "$(code -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-04\",\"stepCode\":\"VISUAL\",\"result\":\"Pass\",\"clientKey\":\"k-4b-$SN\"}")"

echo "=== 7. 返修放行与复检 ==="
RH=$(api -X POST "$BASE/api/production/hold-handle?sn=$SN" -d '{"action":"rework","remark":"返修完成"}')
check "返修后状态" "InProcess" "$(echo "$RH" | jget "d['status']")"
R5=$(api -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-04\",\"stepCode\":\"VISUAL\",\"result\":\"Pass\",\"clientKey\":\"k-5-$SN\"}")
check "复检通过并流转 ICP" "ICT测试" "$(echo "$R5" | jget "d['nextStepName']")"

echo "=== 8. 追溯 ==="
TR=$(api $BASE/api/trace/$SN)
check "追溯命中 SN" "$SN" "$(echo "$TR" | jget "d['sn']")"
check "不良次数 1" "1" "$(echo "$TR" | jget "d['failCount']")"
check "工序履历 9 步" "9" "$(echo "$TR" | jget "len(d['steps'])")"
check "目视工序记录了不良" "虚焊" "$(echo "$TR" | jget "[s for s in d['steps'] if s['stepCode']=='VISUAL'][0]['defectName']" 2>/dev/null || echo)"
check "追溯含操作人" "True" "$(echo "$TR" | jget "bool([s for s in d['steps'] if s['operatorName']][0]['operatorName'])")"

echo "=== 8b. 维修站：进站/出站/重复维修 ==="
SNX="RP$(date +%H%M%S)01"
RP_SCAN() { api -X POST $BASE/api/production/scan -d "{\"sn\":\"$SNX\",\"orderNo\":\"$ORDER\",\"stationCode\":\"$1\",\"stepCode\":\"$2\",\"result\":\"$3\",\"defectCode\":\"$4\",\"clientKey\":\"$5\"}" >/dev/null; }
RP_SCAN A-01 PLUGIN Pass "" "rp-1-$SNX"
RP_SCAN A-02 WAVE Pass "" "rp-2-$SNX"
RP_SCAN A-03 TOUCHUP Pass "" "rp-3-$SNX"
RP_SCAN A-04 VISUAL Fail WELD-01 "rp-4-$SNX"
check "目视判不良后隔离" "True" "$(api $BASE/api/trace/$SNX | jget "d['status']=='Hold'")"

LEADER=$(api -X POST $BASE/api/auth/login -d '{"userNameOrEmail":"leader","password":"Leader@123"}' | jget "d['accessToken']")
P1=$(TOKEN=$LEADER api "$BASE/api/production/repair/pending?limit=50")
check "待维修队列含该 SN" "True" "$(echo "$P1" | jget "any(r['sn']=='$SNX' for r in d)")"
check "队列显示最后不良=虚焊" "虚焊" "$(echo "$P1" | jget "[r['defectName'] for r in d if r['sn']=='$SNX'][0]")"
check "维修次数初始 0" "0" "$(echo "$P1" | jget "[r['repairCount'] for r in d if r['sn']=='$SNX'][0]")"

C1=$(TOKEN=$LEADER api -X POST $BASE/api/production/repair/checkin -d "{\"sn\":\"$SNX\",\"action\":\"resolder\",\"stationCode\":\"A-RP\",\"remark\":\"补焊虚焊点\"}")
check "第 1 次进站成功" "1" "$(echo "$C1" | jget "d['repairNo']")"
check "进站后记录在修" "InProgress" "$(echo "$C1" | jget "d['status']")"
check "重复进站拦截 409" "409" "$(TOKEN=$LEADER code -X POST $BASE/api/production/repair/checkin -d "{\"sn\":\"$SNX\",\"action\":\"resolder\"}")"

O1=$(TOKEN=$LEADER api -X POST $BASE/api/production/repair/checkout -d "{\"sn\":\"$SNX\",\"outcome\":\"repaired\",\"stationCode\":\"A-RP\"}")
check "修复出站回 InProcess" "InProcess" "$(echo "$O1" | jget "d['status']")"
check "出站放回原判不良工序" "True" "$(echo "$O1" | jget "'目视检查' in d['message']")"
check "修复出站后离开待修队列" "True" "$(TOKEN=$LEADER api "$BASE/api/production/repair/pending?limit=50" | jget "not any(r['sn']=='$SNX' for r in d)")"

RP_SCAN A-04 VISUAL Pass "" "rp-5-$SNX"
check "重测目视通过流转 ICT" "ICT测试" "$(api $BASE/api/trace/$SNX | jget "d['currentStepName']")"
RP_SCAN A-05 ICT Fail ELE-01 "rp-6-$SNX"
C2=$(TOKEN=$LEADER api -X POST $BASE/api/production/repair/checkin -d "{\"sn\":\"$SNX\",\"action\":\"replace\",\"remark\":\"更换反馈光耦\"}")
check "重复维修：第 2 次进站" "2" "$(echo "$C2" | jget "d['repairNo']")"
TOKEN=$LEADER api -X POST $BASE/api/production/repair/checkout -d "{\"sn\":\"$SNX\",\"outcome\":\"repaired\"}" >/dev/null
RP_SCAN A-05 ICT Pass "" "rp-7-$SNX"
RP_SCAN A-06 BURNIN Fail ELE-04 "rp-8-$SNX"
check "第 3 次进站被上限拦截 409" "409" "$(TOKEN=$LEADER code -X POST $BASE/api/production/repair/checkin -d "{\"sn\":\"$SNX\",\"action\":\"other\"}")"
C3=$(api -X POST $BASE/api/production/repair/checkin -d "{\"sn\":\"$SNX\",\"action\":\"adjust\",\"force\":true,\"remark\":\"管理员授权第三次维修\"}")
check "管理员 force 第 3 次进站" "3" "$(echo "$C3" | jget "d['repairNo']")"
O3=$(TOKEN=$LEADER api -X POST $BASE/api/production/repair/checkout -d "{\"sn\":\"$SNX\",\"outcome\":\"scrap\"}")
check "维修站报废出站" "Scrapped" "$(echo "$O3" | jget "d['status']")"
TRX=$(api $BASE/api/trace/$SNX)
check "追溯含 3 条维修记录" "3" "$(echo "$TRX" | jget "len(d['repairs'])")"
check "末次维修状态=Scrapped" "Scrapped" "$(echo "$TRX" | jget "d['repairs'][-1]['status']")"
check "SN 终态=Scrapped" "Scrapped" "$(echo "$TRX" | jget "d['status']")"
check "维修站报废记入不良次数" "3" "$(echo "$TRX" | jget "d['failCount']")"

OPT=$(api -X POST $BASE/api/auth/login -d '{"userNameOrEmail":"operator1","password":"Operator@123"}' | jget "d['accessToken']")
check "操作工访问维修站 403" "403" "$(TOKEN=$OPT code $BASE/api/production/repair/pending)"
check "操作工维修进站 403" "403" "$(TOKEN=$OPT code -X POST $BASE/api/production/repair/checkin -d "{\"sn\":\"$SNX\",\"action\":\"other\"}")"

echo "=== 9. 看板聚合 ==="
DB=$(api "$BASE/api/dashboard?lineId=1")
check "今日有合格产出" "True" "$(echo "$DB" | jget "d['todayPass']>0")"
check "小时产出曲线非空" "True" "$(echo "$DB" | jget "len(d['hourly'])>0")"
check "WIP 分布非空" "True" "$(echo "$DB" | jget "len(d['wipByStep'])>0")"

echo "=== 10. 角色权限 ==="
TOKEN=""
OP=$(api -X POST $BASE/api/auth/login -d '{"userNameOrEmail":"operator1","password":"Operator@123"}')
TOKEN=$(echo "$OP" | jget "d['accessToken']")
check "操作工登录" "true" "$([ -n "$TOKEN" ] && echo true || echo false)"
check "操作工访问账号管理 403" "403" "$(code $BASE/api/auth/users)"
check "操作工建单 403" "403" "$(code -X POST $BASE/api/work-orders -d '{"productId":1,"lineId":1,"planQty":5,"priority":2,"plannedStartUtc":"2026-11-10T08:00:00Z","dueDateUtc":"2026-11-11T08:00:00Z"}')"
check "操作工可以扫码" "200" "$(code -X POST $BASE/api/production/scan -d "{\"sn\":\"$SN\",\"orderNo\":\"$ORDER\",\"stationCode\":\"A-05\",\"stepCode\":\"ICT\",\"result\":\"Pass\",\"clientKey\":\"k-op-$SN\"}")"

echo "=== 11. 查询条件（工单日期/产线、维修站工序/不良）==="
TOKEN=$(echo "$LOGIN" | jget "d['accessToken']")
W1=$(api "$BASE/api/work-orders?lineId=2&from=2026-11-01&to=2026-12-31")
check "工单按产线+计划开工日期有结果" "True" "$([ "$(echo "$W1" | jget "len(d)")" -gt 0 ] && echo True || echo False)"
check "筛选结果只含二线" "True" "$(echo "$W1" | jget "all(w['lineId']==2 for w in d)")"
check "日期落在未来 -> 0 张" "0" "$(api "$BASE/api/work-orders?from=2027-01-01" | jget "len(d)")"
check "工单行含返修台数/次数" "True" "$(api "$BASE/api/work-orders" | jget "all('repairUnits' in w and 'repairTimes' in w for w in d)")"

LEADER=$(echo "$LOGIN" | jget "d['accessToken']")
RH=$(TOKEN=$LEADER api "$BASE/api/production/repair/history?limit=100&defectCode=WELD-01")
check "维修历史按不良代码筛选" "True" "$([ "$(echo "$RH" | jget "len(d)")" -gt 0 ] && echo True || echo False)"
check "历史记录不良代码都是虚焊" "True" "$(echo "$RH" | jget "all(r['defectCode']=='WELD-01' for r in d)")"
PS=$(TOKEN=$LEADER api "$BASE/api/production/repair/pending?limit=50&stepId=6")
check "待维修队列按工序筛选（老化测试）" "True" "$(echo "$PS" | jget "all(r['currentStepName']=='老化测试' for r in d)")"
check "待维修队列按产线筛选" "True" "$(TOKEN=$LEADER api "$BASE/api/production/repair/pending?limit=50&lineId=2" | jget "all(r['lineName'].startswith('电源二线') for r in d)")"

echo "=== 12. 工单报告（机种 / 工单下钻）==="
BY_P=$(api "$BASE/api/analysis/yield?by=order&product=SPS-800W")
check "按机种筛报告有数据" "True" "$([ "$(echo "$BY_P" | jget "len(d['rows'])")" -gt 0 ] && echo True || echo False)"
check "报告行都是 800W 机种" "True" "$(echo "$BY_P" | jget "all(r['displayName'].startswith('800W') for r in d['rows'])")"
check "按工单号收窄到一行" "1" "$(api "$BASE/api/analysis/yield?by=order&order=$ORDER" | jget "len(d['rows'])")"

echo "=== 13. QC 抽检（已包装成品）==="
QCQ=$(api "$BASE/api/qc/inspections?limit=5")
check "抽检台账有历史样本" "True" "$([ "$(echo "$QCQ" | jget "d['summary']['inspected']")" -gt 0 ] && echo True || echo False)"
CAND=$(api "$BASE/api/qc/candidates?limit=1")
QSN=$(echo "$CAND" | jget "d[0]['sn']")
check "候选为已包装成品" "True" "$([ -n "$QSN" ] && echo True || echo False)"
QC1=$(api -X POST $BASE/api/qc/inspections -d "{\"sn\":\"$QSN\",\"category\":\"包装\",\"result\":\"Pass\",\"remark\":\"冒烟测试抽检\"}")
check "录入抽检记录成功" "Pass" "$(echo "$QC1" | jget "d['result']")"
check "不合格必须带不良代码 400" "400" "$(code -X POST $BASE/api/qc/inspections -d "{\"sn\":\"$QSN\",\"category\":\"外观\",\"result\":\"Fail\"}")"
check "未完工 SN 不能抽检 409" "409" "$(code -X POST $BASE/api/qc/inspections -d "{\"sn\":\"$SN\",\"category\":\"包装\",\"result\":\"Pass\"}")"
check "操作工录入抽检 403" "403" "$(TOKEN=$OPT code -X POST $BASE/api/qc/inspections -d "{\"sn\":\"$QSN\",\"category\":\"包装\",\"result\":\"Pass\"}")"
check "抽检后候选里能查到已抽次数" "True" "$(api "$BASE/api/qc/candidates?order=$(api "$BASE/api/qc/inspections?limit=1" | jget "d['rows'][0]['orderNo']")&limit=200" | jget "any(r['inspectedTimes']>0 for r in d)")"

echo
echo "结果：通过 $pass / 失败 $fail"
[ "$fail" -eq 0 ]
