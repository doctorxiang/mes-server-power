<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import AppShell from '@/components/AppShell.vue'
import { mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import { readStation, saveStation } from '@/api/session'
import type { MasterBootstrap, RepairHistoryRow, RepairPendingRow, RepairRecordRow, TraceResponse } from '@/types'
import { REPAIR_ACTION_LABELS, REPAIR_STATUS_LABELS } from '@/types'
import { formatTime } from '@/utils/format'

const boot = ref<MasterBootstrap | null>(null)
const queue = ref<RepairPendingRow[]>([])
const snInput = ref('')
const unit = ref<TraceResponse | null>(null)
const loading = ref(true)
const lookuping = ref(false)
const submitting = ref(false)
const loadError = ref('')
const feedback = ref<{ kind: 'ok' | 'fail' | 'info'; title: string; detail: string } | null>(null)

const snRef = ref<HTMLInputElement | null>(null)

// 进站表单
const action = ref('Resolder')
const defectCode = ref('')
const checkinRemark = ref('')
// 出站表单
const checkoutRemark = ref('')

const repairStations = computed(() => (boot.value?.workstations ?? []).filter((w) => w.code.endsWith('-RP')))
const stationCode = ref(readStation())
const openRepair = computed<RepairRecordRow | null>(
  () => unit.value?.repairs.find((r) => r.status === 'InProgress') ?? null,
)
const repairCount = computed(() => unit.value?.repairs.length ?? 0)
const lastFailDefect = computed(() => {
  const name = unit.value?.steps.filter((s) => s.result === 'Fail').at(-1)?.defectName
  return boot.value?.defectCodes.find((d) => d.name === name) ?? null
})
const isHold = computed(() => unit.value?.status === 'Hold')

// 队列筛选：日期（隔离时间）/ 产线 / 工序 / 不良代码 / 工单号
// 不做"点 SN 自动查询"，避免维修员误触把正在看的单子顶掉
const tab = ref<'pending' | 'history'>('pending')
const filterOrderNo = ref('')
const filterLineId = ref(0)
const filterStepId = ref(0)
const filterDefectCode = ref('')
const filterFrom = ref('')
const filterTo = ref('')
const filterStatus = ref('')
const queueLoading = ref(false)
const history = ref<RepairHistoryRow[]>([])
const historyLoading = ref(false)

const hasFilter = computed(() =>
  Boolean(filterOrderNo.value || filterLineId.value || filterStepId.value || filterDefectCode.value || filterFrom.value || filterTo.value || filterStatus.value),
)

function queryParams() {
  return {
    limit: 100,
    orderNo: filterOrderNo.value.trim() || undefined,
    lineId: filterLineId.value || undefined,
    stepId: filterStepId.value || undefined,
    defectCode: filterDefectCode.value || undefined,
    from: filterFrom.value || undefined,
    to: filterTo.value || undefined,
  }
}

async function loadQueue() {
  queueLoading.value = true
  try {
    queue.value = (await mesApi.repairPending(queryParams())).data
  } catch {
    // 队列刷新失败不打断操作
  } finally {
    queueLoading.value = false
  }
}

async function loadHistory() {
  historyLoading.value = true
  try {
    history.value = (
      await mesApi.repairHistory({ ...queryParams(), status: filterStatus.value || undefined })
    ).data
  } catch (e) {
    feedback.value = { kind: 'fail', title: '维修记录查询失败', detail: parseApiError(e) }
  } finally {
    historyLoading.value = false
  }
}

async function switchTab(next: 'pending' | 'history') {
  tab.value = next
  if (next === 'history') await loadHistory()
  else await loadQueue()
}

async function search() {
  if (tab.value === 'history') await loadHistory()
  else await loadQueue()
}

function resetFilters() {
  filterOrderNo.value = ''
  filterLineId.value = 0
  filterStepId.value = 0
  filterDefectCode.value = ''
  filterFrom.value = ''
  filterTo.value = ''
  filterStatus.value = ''
  search()
}

async function loadAll() {
  loading.value = true
  loadError.value = ''
  try {
    const [{ data: master }] = await Promise.all([mesApi.bootstrap()])
    boot.value = master
    if (!stationCode.value && master.workstations.some((w) => w.code === 'A-RP')) {
      stationCode.value = 'A-RP'
    }
    await loadQueue()
  } catch (e) {
    loadError.value = parseApiError(e)
  } finally {
    loading.value = false
    await nextTick()
    snRef.value?.focus()
  }
}

async function lookup(sn?: string) {
  const code = (sn ?? snInput.value).trim().toUpperCase()
  if (!code) return
  lookuping.value = true
  feedback.value = null
  try {
    const { data } = await mesApi.trace(code)
    unit.value = data
    snInput.value = code
    // 预填：默认取最近一次不良代码，措施默认补焊
    if (!openRepair.value) {
      defectCode.value = lastFailDefect.value?.code ?? ''
    }
    nextTick(() => snRef.value?.focus())
  } catch (e) {
    unit.value = null
    feedback.value = { kind: 'fail', title: 'SN 查询失败', detail: parseApiError(e) }
  } finally {
    lookuping.value = false
  }
}

async function checkin(force = false) {
  if (!unit.value || submitting.value) return
  submitting.value = true
  try {
    const { data } = await mesApi.repairCheckin({
      sn: unit.value.sn,
      action: action.value,
      defectCode: defectCode.value || undefined,
      remark: checkinRemark.value.trim() || undefined,
      stationCode: stationCode.value || undefined,
      force,
    })
    feedback.value = {
      kind: 'ok',
      title: `进站成功（第 ${data.repairNo} 次维修）`,
      detail: `不良工序「${data.sourceStepName}」${data.defectName ? ` · 不良：${data.defectName}` : ''}，维修完成后请出站`,
    }
    checkinRemark.value = ''
    await reload(unit.value.sn)
  } catch (e) {
    feedback.value = { kind: 'fail', title: '进站失败', detail: parseApiError(e) }
  } finally {
    submitting.value = false
  }
}

async function checkout(outcome: 'repaired' | 'scrap') {
  if (!unit.value || submitting.value) return
  submitting.value = true
  try {
    const { data } = await mesApi.repairCheckout({
      sn: unit.value.sn,
      outcome,
      remark: checkoutRemark.value.trim() || undefined,
      stationCode: stationCode.value || undefined,
    })
    feedback.value = { kind: outcome === 'scrap' ? 'info' : 'ok', title: '出站成功', detail: data.message }
    checkoutRemark.value = ''
    await reload(unit.value.sn)
  } catch (e) {
    feedback.value = { kind: 'fail', title: '出站失败', detail: parseApiError(e) }
  } finally {
    submitting.value = false
  }
}

async function reload(sn: string) {
  const { data } = await mesApi.trace(sn)
  unit.value = data
  await loadQueue()
}

function onStationChange() {
  saveStation(stationCode.value)
}

let queueTimer: number | undefined
onMounted(() => {
  loadAll()
  // 只在"待维修"Tab 自动刷新：翻历史时列表突然跳动很难受
  queueTimer = window.setInterval(() => {
    if (tab.value === 'pending') loadQueue()
  }, 15000)
})
onBeforeUnmount(() => {
  if (queueTimer) window.clearInterval(queueTimer)
})
</script>

<template>
  <AppShell title="维修站" subtitle="不良 SN 进出站管理：进站确认维修 → 修复放回原工序重测 / 报废">
    <div v-if="loading" class="empty">加载中…</div>
    <div v-else-if="loadError" class="alert alert-error">{{ loadError }}</div>

    <template v-else>
      <div class="repair-grid">
        <!-- 左：扫码与操作 -->
        <section class="card">
          <div class="card-body">
            <div class="row" style="gap: 10px">
              <select v-model="stationCode" class="input" style="max-width: 160px" @change="onStationChange">
                <option v-for="s in repairStations" :key="s.code" :value="s.code">{{ s.name }}</option>
              </select>
              <input
                ref="snRef"
                v-model="snInput"
                class="input input-lg"
                style="flex: 1"
                placeholder="扫描或输入 SN，回车查询"
                autocomplete="off"
                spellcheck="false"
                @keyup.enter="lookup()"
              />
              <button class="btn btn-primary" style="height: 54px; min-width: 96px" :disabled="lookuping" @click="lookup()">
                {{ lookuping ? '查询中…' : '查询' }}
              </button>
            </div>
            <div class="small muted" style="margin-top: 8px">
              扫码后自动带出不良工序与不良代码；仅班组长 / 质检 / 管理员可操作维修进出站。
            </div>
          </div>
        </section>

        <div v-if="feedback" class="alert alert-card" :class="{ 'alert-error': feedback.kind === 'fail', 'alert-ok': feedback.kind === 'ok' }">
          <strong>{{ feedback.title }}</strong>
          <div class="small" style="margin-top: 4px">{{ feedback.detail }}</div>
        </div>

        <!-- SN 详情 -->
        <section v-if="unit" class="card span-2">
          <div class="card-head">
            <h3><span class="mono">{{ unit.sn }}</span></h3>
            <span class="small muted">
              {{ unit.productName }} · {{ unit.lineName }} · 工单 <span class="mono">{{ unit.orderNo }}</span>
              · 历史维修 <strong :class="{ 'rep-warn': repairCount > 0 }">{{ repairCount }}</strong> 次
            </span>
          </div>
          <div class="card-body">
            <div class="unit-state">
              <span class="badge" :class="{ 'badge-danger': isHold, 'badge-ok': unit.status === 'InProcess' }">
                {{ unit.status === 'Hold' ? '不良隔离中' : unit.status === 'InProcess' ? '产线在制' : unit.status }}
              </span>
              <span class="small">当前工序「{{ unit.currentStepName }}」</span>
              <span v-if="openRepair" class="badge badge-warn">第 {{ openRepair.repairNo }} 次维修中</span>
            </div>

            <!-- 进站表单 -->
            <template v-if="isHold && !openRepair">
              <div class="form-grid">
                <label>
                  <span class="small muted">维修措施</span>
                  <select v-model="action" class="input">
                    <option value="Replace">换料</option>
                    <option value="Resolder">补焊</option>
                    <option value="Adjust">调整 / 重插</option>
                    <option value="Other">其它</option>
                  </select>
                </label>
                <label>
                  <span class="small muted">不良代码（默认取最近不良）</span>
                  <select v-model="defectCode" class="input">
                    <option value="">（不指定）</option>
                    <option v-for="d in boot?.defectCodes ?? []" :key="d.code" :value="d.code">
                      {{ d.code }} · {{ d.name }}
                    </option>
                  </select>
                </label>
                <label>
                  <span class="small muted">维修说明</span>
                  <input v-model="checkinRemark" class="input" maxlength="256" placeholder="可选，例如：补焊 U12 虚焊点" @keyup.enter="checkin()" />
                </label>
              </div>
              <div class="row" style="gap: 10px; margin-top: 14px">
                <button class="btn btn-primary" style="min-width: 150px" :disabled="submitting" @click="checkin()">
                  确认进站维修
                </button>
                <span class="small muted">出站后 SN 将放回「{{ unit.currentStepName }}」重新过站</span>
              </div>
            </template>

            <!-- 出站表单 -->
            <template v-else-if="isHold && openRepair">
              <div class="open-repair">
                <div class="small muted">第 {{ openRepair.repairNo }} 次维修 · {{ formatTime(openRepair.startedAtUtc) }} 进站</div>
                <div style="margin-top: 6px">
                  <span class="badge">{{ REPAIR_ACTION_LABELS[openRepair.action] ?? openRepair.action }}</span>
                  <span v-if="openRepair.defectName" class="badge badge-warn">{{ openRepair.defectName }}</span>
                  <span class="small muted" style="margin-left: 8px">
                    {{ openRepair.operatorName }}{{ openRepair.stationCode ? ` · ${openRepair.stationCode}` : '' }}
                  </span>
                </div>
                <div v-if="openRepair.remark" class="small" style="margin-top: 6px">{{ openRepair.remark }}</div>
              </div>
              <div class="form-grid" style="margin-top: 12px">
                <label>
                  <span class="small muted">出站备注</span>
                  <input v-model="checkoutRemark" class="input" maxlength="256" placeholder="可选，例如：更换 U12 / 复测通过" @keyup.enter="checkout('repaired')" />
                </label>
              </div>
              <div class="row" style="gap: 10px; margin-top: 14px">
                <button class="btn btn-primary" style="min-width: 200px" :disabled="submitting" @click="checkout('repaired')">
                  修复完成，放回原工序重测
                </button>
                <button class="btn btn-danger" :disabled="submitting" @click="checkout('scrap')">报废</button>
              </div>
            </template>

            <!-- 非隔离状态 -->
            <div v-else class="small muted" style="margin-top: 10px">
              该 SN 当前不在不良隔离状态，无需维修站操作；如需查看完整履历请前往 SN 追溯。
            </div>

            <!-- 历史维修记录 -->
            <template v-if="unit.repairs.length">
              <div class="small muted" style="margin: 16px 0 8px">历史维修记录</div>
              <table class="table">
                <thead>
                  <tr>
                    <th>次数</th>
                    <th>不良工序</th>
                    <th>不良</th>
                    <th>措施</th>
                    <th>状态</th>
                    <th>进站</th>
                    <th>出站</th>
                    <th>维修人</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="r in [...unit.repairs].reverse()" :key="r.id">
                    <td class="mono">#{{ r.repairNo }}</td>
                    <td>{{ r.sourceStepName }}</td>
                    <td>{{ r.defectName ?? '—' }}</td>
                    <td>{{ REPAIR_ACTION_LABELS[r.action] ?? r.action }}</td>
                    <td>
                      <span class="badge" :class="{ 'badge-warn': r.status === 'InProgress', 'badge-ok': r.status === 'Repaired', 'badge-danger': r.status === 'Scrapped' }">
                        {{ REPAIR_STATUS_LABELS[r.status] }}
                      </span>
                    </td>
                    <td class="small">{{ formatTime(r.startedAtUtc) }} · {{ r.operatorName }}</td>
                    <td class="small">{{ r.completedAtUtc ? `${formatTime(r.completedAtUtc)} · ${r.checkoutByName}` : '—' }}</td>
                    <td class="small muted">{{ r.remark ?? '—' }}</td>
                  </tr>
                </tbody>
              </table>
            </template>
          </div>
        </section>

        <!-- 右/下：待维修队列 / 维修记录 -->
        <section class="card span-2">
          <div class="card-head">
            <h3>
              维修查询
              <span class="small muted" style="font-weight: 400">
                {{ tab === 'pending' ? `待维修 ${queue.length} 台` : `维修记录 ${history.length} 条` }}
              </span>
            </h3>
            <div class="tabs" style="margin: 0; border: none">
              <button class="tab" :class="{ active: tab === 'pending' }" @click="switchTab('pending')">待维修队列</button>
              <button class="tab" :class="{ active: tab === 'history' }" @click="switchTab('history')">维修记录</button>
            </div>
          </div>
          <div class="card-body">
            <div class="queue-filters">
              <label>
                <span class="small muted">工单号</span>
                <input v-model="filterOrderNo" class="input" placeholder="如 WO2026" @keyup.enter="search()" />
              </label>
              <label>
                <span class="small muted">产线</span>
                <select v-model.number="filterLineId" class="input" @change="search()">
                  <option :value="0">全部产线</option>
                  <option v-for="l in boot?.lines ?? []" :key="l.id" :value="l.id">{{ l.name }}</option>
                </select>
              </label>
              <label>
                <span class="small muted">{{ tab === 'pending' ? '隔离所在工序' : '判不良工序' }}</span>
                <select v-model.number="filterStepId" class="input" @change="search()">
                  <option :value="0">全部工序</option>
                  <option v-for="s in boot?.steps ?? []" :key="s.id" :value="s.id">{{ s.seq }}. {{ s.name }}</option>
                </select>
              </label>
              <label>
                <span class="small muted">不良代码</span>
                <select v-model="filterDefectCode" class="input" @change="search()">
                  <option value="">全部不良</option>
                  <option v-for="d in boot?.defectCodes ?? []" :key="d.code" :value="d.code">{{ d.code }} · {{ d.name }}</option>
                </select>
              </label>
              <label>
                <span class="small muted">{{ tab === 'pending' ? '隔离日期 从' : '进站日期 从' }}</span>
                <input v-model="filterFrom" type="date" class="input" @change="search()" />
              </label>
              <label>
                <span class="small muted">到</span>
                <input v-model="filterTo" type="date" class="input" @change="search()" />
              </label>
              <label v-if="tab === 'history'">
                <span class="small muted">结果</span>
                <select v-model="filterStatus" class="input" @change="search()">
                  <option value="">全部</option>
                  <option value="InProgress">维修中</option>
                  <option value="Repaired">修复出站</option>
                  <option value="Scrapped">报废出站</option>
                </select>
              </label>
              <button class="btn" :disabled="queueLoading || historyLoading" @click="search()">
                {{ queueLoading || historyLoading ? '查询中…' : '查询' }}
              </button>
              <button class="btn btn-ghost" :disabled="!hasFilter" @click="resetFilters">重置</button>
            </div>

            <template v-if="tab === 'pending'">
              <div v-if="!queue.length" class="empty">
                {{ hasFilter ? '该条件下没有待维修的 SN' : '当前没有待维修的 SN' }}
              </div>
              <table v-else class="table">
                <thead>
                  <tr>
                    <th>SN</th>
                    <th>工单</th>
                    <th>机种</th>
                    <th>产线</th>
                    <th>不良工序</th>
                    <th>不良代码</th>
                    <th>已维修</th>
                    <th>隔离时间</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="row in queue" :key="row.unitId">
                    <td class="mono">
                      {{ row.sn }}
                      <span v-if="row.repairing" class="badge badge-warn" style="margin-left: 6px">维修中</span>
                    </td>
                    <td class="mono">{{ row.orderNo }}</td>
                    <td>{{ row.productName }}</td>
                    <td>{{ row.lineName }}</td>
                    <td>{{ row.currentStepName }}</td>
                    <td>
                      <span v-if="row.defectName" class="badge badge-warn">{{ row.defectCode }} · {{ row.defectName }}</span>
                      <span v-else class="muted small">—</span>
                    </td>
                    <td><span :class="{ 'rep-warn': row.repairCount > 0 }">{{ row.repairCount }} 次</span></td>
                    <td class="small muted">{{ formatTime(row.failedAtUtc) }}</td>
                  </tr>
                </tbody>
              </table>
            </template>

            <template v-else>
              <div v-if="!history.length" class="empty">
                {{ hasFilter ? '该条件下没有维修记录' : '还没有维修记录' }}
              </div>
              <table v-else class="table">
                <thead>
                  <tr>
                    <th>SN</th>
                    <th>工单</th>
                    <th>机种 / 产线</th>
                    <th>不良工序</th>
                    <th>不良代码</th>
                    <th>措施</th>
                    <th>结果</th>
                    <th>进站 / 出站</th>
                    <th>人</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="r in history" :key="r.id">
                    <td class="mono">{{ r.sn }}<span class="small muted"> · #{{ r.repairNo }}</span></td>
                    <td class="mono">{{ r.orderNo }}</td>
                    <td class="small">
                      <div>{{ r.productName }}</div>
                      <div class="muted">{{ r.lineName }}</div>
                    </td>
                    <td class="small">{{ r.sourceStepName }}</td>
                    <td>
                      <span v-if="r.defectName" class="badge badge-warn">{{ r.defectCode }} · {{ r.defectName }}</span>
                      <span v-else class="muted small">—</span>
                    </td>
                    <td class="small">{{ REPAIR_ACTION_LABELS[r.action] ?? r.action }}</td>
                    <td>
                      <span class="badge" :class="{
                        'badge-warn': r.status === 'InProgress',
                        'badge-ok': r.status === 'Repaired',
                        'badge-danger': r.status === 'Scrapped',
                      }">{{ REPAIR_STATUS_LABELS[r.status] }}</span>
                    </td>
                    <td class="small muted">
                      <div>{{ formatTime(r.startedAtUtc) }}</div>
                      <div>{{ r.completedAtUtc ? formatTime(r.completedAtUtc) : '未出站' }}</div>
                    </td>
                    <td class="small muted">
                      <div>{{ r.operatorName }}</div>
                      <div>{{ r.checkoutByName ?? '—' }}</div>
                    </td>
                  </tr>
                </tbody>
              </table>
            </template>
          </div>
        </section>
      </div>
    </template>
  </AppShell>
</template>

<style scoped>
.repair-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 16px;
}

@media (min-width: 900px) {
  .repair-grid {
    grid-template-columns: 1fr;
  }
}

.span-2 {
  grid-column: 1 / -1;
}

.alert-card {
  border-left: 4px solid var(--primary);
}

.alert-ok {
  border-color: #16a34a;
  background: rgba(22, 163, 74, 0.08);
}

.alert-error {
  border-left: 4px solid #dc2626;
}

.unit-state {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
  margin-top: 14px;
}

.form-grid label {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.open-repair {
  padding: 12px 14px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--surface-2);
}

.rep-warn {
  color: #d97706;
  font-weight: 700;
}

.row-click tbody tr {
  cursor: pointer;
}

.queue-filters {
  display: flex;
  gap: 10px;
  align-items: flex-end;
  flex-wrap: wrap;
  margin-bottom: 14px;
}

.queue-filters label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 180px;
}
</style>
