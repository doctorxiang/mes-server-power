<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import AppShell from '@/components/AppShell.vue'
import { mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import { readStation, saveStation } from '@/api/session'
import type { DefectCode, MasterBootstrap, RecentLog, ScanResponse, WorkOrderListItem } from '@/types'
import { formatTime } from '@/utils/format'

const boot = ref<MasterBootstrap | null>(null)
const stationCode = ref(readStation())
const order = ref<WorkOrderListItem | null>(null)
const orderInput = ref('')
const snInput = ref('')
const submitting = ref(false)
const loading = ref(true)
const loadError = ref('')

const activeOrders = ref<WorkOrderListItem[]>([])
const recent = ref<RecentLog[]>([])
const todayPass = ref(0)
const todayFail = ref(0)

const feedback = ref<{ kind: 'ok' | 'fail' | 'info'; title: string; detail: string } | null>(null)
const ngOpen = ref(false)
const ngRemark = ref('')
const lastScan = ref<ScanResponse | null>(null)

const snRef = ref<HTMLInputElement | null>(null)

const stations = computed(() => boot.value?.workstations ?? [])
const currentStation = computed(() => stations.value.find((s) => s.code === stationCode.value) ?? null)
const step = computed(() => {
  const st = currentStation.value
  if (!st?.stepId) return null
  return boot.value?.steps.find((s) => s.id === st.stepId) ?? null
})
const stationLine = computed(() => boot.value?.lines.find((l) => l.id === currentStation.value?.lineId) ?? null)

const totalSteps = computed(() => boot.value?.products.find((p) => p.code === order.value?.productCode)?.route.length ?? 9)

const defectsByCategory = computed(() => {
  const groups = new Map<string, DefectCode[]>()
  for (const d of boot.value?.defectCodes ?? []) {
    const list = groups.get(d.category) ?? []
    list.push(d)
    groups.set(d.category, list)
  }
  return [...groups.entries()]
})

let recentTimer: number | undefined

async function loadAll() {
  loading.value = true
  loadError.value = ''
  try {
    const [{ data: master }, { data: orders }] = await Promise.all([mesApi.bootstrap(), mesApi.workOrders()])
    boot.value = master
    activeOrders.value = orders.filter((o) => o.status === 'Released' || o.status === 'InProduction')

    if (!stationCode.value && master.workstations.length) {
      stationCode.value = master.workstations[0].code
    }
    // 工位默认带出同产线的在制工单，减少现场操作
    if (!order.value && activeOrders.value.length) {
      const sameLine = activeOrders.value.find((o) => o.lineId === currentStation.value?.lineId)
      order.value = sameLine ?? activeOrders.value[0]
    }

    await refreshRecent()
  } catch (e) {
    loadError.value = parseApiError(e)
  } finally {
    loading.value = false
    await nextTick()
    snRef.value?.focus()
  }
}

async function refreshRecent() {
  try {
    const { data } = await mesApi.recent(step.value?.code, 10)
    recent.value = data
  } catch {
    // 列表刷新失败不打断报工
  }
}

function onStationChange() {
  saveStation(stationCode.value)
  todayPass.value = 0
  todayFail.value = 0
  refreshRecent()
  snRef.value?.focus()
}

function pickOrder(o: WorkOrderListItem) {
  order.value = o
  feedback.value = null
  nextTick(() => snRef.value?.focus())
}

async function scanOrder() {
  const code = orderInput.value.trim().toUpperCase()
  if (!code) return
  try {
    const { data } = await mesApi.workOrderByNo(code)
    if (data.status !== 'Released' && data.status !== 'InProduction') {
      feedback.value = { kind: 'fail', title: '工单不可报工', detail: `工单 ${data.orderNo} 状态为 ${data.status}` }
      return
    }
    order.value = data
    orderInput.value = ''
    feedback.value = { kind: 'info', title: '已切换工单', detail: `${data.orderNo} · ${data.productName} · 计划 ${data.planQty}` }
    nextTick(() => snRef.value?.focus())
  } catch (e) {
    feedback.value = { kind: 'fail', title: '工单识别失败', detail: parseApiError(e) }
  }
}

function beep(ok: boolean) {
  try {
    const Ctx = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    const ctx = new Ctx()
    const osc = ctx.createOscillator()
    const gain = ctx.createGain()
    osc.type = ok ? 'sine' : 'square'
    osc.frequency.value = ok ? 880 : 240
    gain.gain.value = 0.05
    osc.connect(gain)
    gain.connect(ctx.destination)
    osc.start()
    osc.stop(ctx.currentTime + (ok ? 0.08 : 0.24))
    setTimeout(() => ctx.close(), 400)
  } catch {
    // 浏览器限制音频时静默
  }
}

async function submit(result: 'Pass' | 'Fail', defectCode?: string) {
  const sn = snInput.value.trim().toUpperCase()
  if (!sn) {
    feedback.value = { kind: 'fail', title: '请扫描 SN', detail: '光标已回到扫码框，请扫描产品条码' }
    return
  }
  if (!order.value) {
    feedback.value = { kind: 'fail', title: '请先选择工单', detail: '扫描工单条码或从列表中选择' }
    return
  }
  if (!currentStation.value) {
    feedback.value = { kind: 'fail', title: '请选择工位', detail: '工位决定当前报工的工序' }
    return
  }

  submitting.value = true
  try {
    const { data } = await mesApi.scan({
      sn,
      orderNo: order.value.orderNo,
      stationCode: currentStation.value.code,
      stepCode: step.value?.code ?? '',
      result,
      defectCode,
      remark: result === 'Fail' ? ngRemark.value.trim() || undefined : undefined,
      clientKey: crypto.randomUUID(),
    })

    lastScan.value = data
    todayPass.value = data.todayStepPassQty
    todayFail.value = data.todayStepFailQty

    if (data.duplicated) {
      feedback.value = { kind: 'info', title: '重复扫码', detail: data.message }
      beep(true)
    } else if (!data.accepted) {
      feedback.value = { kind: 'info', title: '无需报工', detail: data.message }
      beep(true)
    } else if (result === 'Fail') {
      feedback.value = {
        kind: 'fail',
        title: `已记录不良 · ${data.stepName}`,
        detail: `${data.message}（第 ${data.seq}/${data.totalSteps} 站）`,
      }
      beep(false)
    } else {
      feedback.value = {
        kind: 'ok',
        title: data.nextStepName ? `合格放行 → ${data.nextStepName}` : '全部工序完成',
        detail: `${data.sn} · 第 ${data.seq}/${data.totalSteps} 站 · ${data.message}`,
      }
      beep(true)
    }

    snInput.value = ''
    ngRemark.value = ''
    ngOpen.value = false
    await refreshRecent()
  } catch (e) {
    feedback.value = { kind: 'fail', title: '报工被拦截', detail: parseApiError(e) }
    beep(false)
  } finally {
    submitting.value = false
    await nextTick()
    snRef.value?.focus()
  }
}

function onSnEnter() {
  const value = snInput.value.trim().toUpperCase()
  // 扫码枪先扫工单条码（WO 开头）时，自动切换工单
  if (/^WO\d{8,}/.test(value)) {
    orderInput.value = value
    snInput.value = ''
    scanOrder()
    return
  }
  submit('Pass')
}

function openNg() {
  ngOpen.value = true
  ngRemark.value = ''
}

function submitNg(code: string) {
  submit('Fail', code)
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'F2') {
    e.preventDefault()
    if (!ngOpen.value) openNg()
  }
  if (e.key === 'Escape' && ngOpen.value) ngOpen.value = false
}

watch(step, () => {
  todayPass.value = 0
  todayFail.value = 0
  refreshRecent()
})

onMounted(() => {
  loadAll()
  window.addEventListener('keydown', onKeydown)
  recentTimer = window.setInterval(refreshRecent, 15000)
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  if (recentTimer) window.clearInterval(recentTimer)
})
</script>

<template>
  <AppShell title="扫码报工" subtitle="扫码枪为键盘输入，回车即提交；F2 快速判定不良">
    <div v-if="loadError" class="alert alert-error" style="margin-bottom: 16px">{{ loadError }}</div>
    <div v-if="loading" class="empty">正在加载主数据…</div>

    <div v-else class="scan-layout">
      <div class="scan-main">
        <!-- 当前工位 -->
        <section class="card">
          <div class="card-body station-row">
            <div class="field" style="min-width: 210px">
              <label>当前工位</label>
              <select v-model="stationCode" class="select" @change="onStationChange">
                <option v-for="st in stations" :key="st.code" :value="st.code">
                  {{ st.code }} · {{ st.name }}
                </option>
              </select>
            </div>
            <div class="station-meta">
              <div class="chip-line">{{ stationLine?.name ?? '—' }}</div>
              <div class="step-now">
                <span class="step-badge">{{ step?.seq ?? '-' }}/{{ totalSteps }}</span>
                <strong>{{ step?.name ?? '未绑定工序' }}</strong>
              </div>
            </div>
            <div class="station-counters">
              <div class="cnt ok">
                <span>本工序合格</span>
                <strong>{{ todayPass }}</strong>
              </div>
              <div class="cnt bad">
                <span>本工序不良</span>
                <strong>{{ todayFail }}</strong>
              </div>
            </div>
          </div>
        </section>

        <!-- 工单 -->
        <section class="card">
          <div class="card-head">
            <h3>工单</h3>
            <span class="small muted">扫工单条码或点选</span>
          </div>
          <div class="card-body">
            <div class="row" style="gap: 8px">
              <input
                v-model="orderInput"
                class="input mono"
                placeholder="扫描 / 输入工单号，回车确认"
                @keyup.enter="scanOrder"
              />
              <button class="btn" @click="scanOrder">切换</button>
            </div>

            <div v-if="order" class="order-current">
              <div>
                <div class="mono order-no">{{ order.orderNo }}</div>
                <div class="small muted">{{ order.productCode }} · {{ order.productName }}</div>
              </div>
              <div class="order-progress">
                <span>{{ order.completedQty }} / {{ order.planQty }}</span>
                <div class="progress" style="width: 130px">
                  <span :style="{ width: `${Math.min(100, (order.completedQty / order.planQty) * 100)}%` }"></span>
                </div>
              </div>
            </div>

            <div class="order-chips">
              <button
                v-for="o in activeOrders"
                :key="o.id"
                class="order-chip"
                :class="{ active: order?.id === o.id }"
                @click="pickOrder(o)"
              >
                <b class="mono">{{ o.orderNo.slice(-6) }}</b>
                <span>{{ o.productCode }}</span>
              </button>
            </div>
          </div>
        </section>

        <!-- 扫码 -->
        <section class="card scan-card">
          <div class="card-body">
            <label class="scan-label">扫描产品 SN</label>
            <input
              ref="snRef"
              v-model="snInput"
              class="input input-lg"
              placeholder="请扫描 SN 条码…"
              autocomplete="off"
              autocapitalize="characters"
              spellcheck="false"
              @keyup.enter="onSnEnter"
            />

            <div class="scan-actions">
              <button class="btn btn-primary scan-btn" :disabled="submitting" @click="submit('Pass')">
                <span class="key">Enter</span> 合格放行
              </button>
              <button class="btn btn-danger scan-btn" :disabled="submitting" @click="openNg">
                <span class="key">F2</span> 判定不良
              </button>
            </div>

            <div class="scan-hint">
              首站扫码自动建立 SN 档案；后续工序严格按路线推进，跳站与混单会被拦截。
              <span v-if="!step?.isInspection">当前工序为普通装配工序，一般只需合格放行。</span>
            </div>
          </div>
        </section>

        <!-- 结果反馈 -->
        <transition name="pop">
          <section v-if="feedback" class="feedback" :class="feedback.kind">
            <div class="fb-icon">
              <svg v-if="feedback.kind === 'ok'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round">
                <path d="m4 12.5 5 5L20 6" />
              </svg>
              <svg v-else-if="feedback.kind === 'fail'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round">
                <path d="M6 6l12 12M18 6 6 18" />
              </svg>
              <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round">
                <circle cx="12" cy="12" r="9" />
                <path d="M12 8h.01M11 12h1v5h1" />
              </svg>
            </div>
            <div>
              <div class="fb-title">{{ feedback.title }}</div>
              <div class="fb-detail">{{ feedback.detail }}</div>
            </div>
            <button class="btn btn-sm btn-ghost" style="margin-left: auto" @click="feedback = null">关闭</button>
          </section>
        </transition>

        <section v-if="lastScan" class="last-scan card">
          <div class="card-body row between">
            <div class="row" style="gap: 14px">
              <span class="badge" :class="lastScan.unitStatus === 'Completed' ? 'badge-ok' : lastScan.unitStatus === 'Hold' ? 'badge-danger' : 'badge-blue'">
                {{ lastScan.unitStatus === 'Completed' ? '已完工' : lastScan.unitStatus === 'Hold' ? '不良隔离' : '在制' }}
              </span>
              <span class="mono small">{{ lastScan.sn }}</span>
            </div>
            <div class="small muted">
              工序 {{ lastScan.seq }}/{{ lastScan.totalSteps }} · {{ lastScan.stepName }}
              <template v-if="lastScan.nextStepName"> → {{ lastScan.nextStepName }}</template>
            </div>
          </div>
        </section>
      </div>

      <!-- 右栏：最近过站 -->
      <aside class="scan-side card">
        <div class="card-head">
          <h3>最近过站</h3>
          <span class="badge">{{ step?.name ?? '全部' }}</span>
        </div>
        <div class="log-list">
          <div v-if="!recent.length" class="empty">暂无过站记录</div>
          <div v-for="log in recent" :key="log.id" class="log-item">
            <span class="log-dot" :class="log.result === 'Pass' ? 'pass' : 'fail'"></span>
            <div style="min-width: 0; flex: 1">
              <div class="mono log-sn">{{ log.sn }}</div>
              <div class="small muted">
                {{ log.operatorName }} · {{ log.stationCode }} · {{ formatTime(log.createdAtUtc) }}
                <template v-if="log.defectName"> · <span style="color: var(--danger)">{{ log.defectName }}</span></template>
              </div>
            </div>
            <span class="badge" :class="log.result === 'Pass' ? 'badge-ok' : 'badge-danger'">
              {{ log.result === 'Pass' ? '合格' : '不良' }}
            </span>
          </div>
        </div>
      </aside>
    </div>

    <!-- 不良选择 -->
    <div v-if="ngOpen" class="modal-mask" @click.self="ngOpen = false">
      <div class="modal" style="max-width: 620px">
        <div class="modal-head">
          <div>
            <h3>判定不良</h3>
            <div class="small muted">SN：<span class="mono">{{ snInput || '未扫描' }}</span> · 工序：{{ step?.name }}</div>
          </div>
          <button class="btn btn-sm btn-ghost" @click="ngOpen = false">关闭</button>
        </div>
        <div class="modal-body">
          <div v-for="[category, list] in defectsByCategory" :key="category">
            <div class="defect-cat">{{ category }}</div>
            <div class="defect-grid">
              <button v-for="d in list" :key="d.id" class="defect-btn" :disabled="submitting" @click="submitNg(d.code)">
                <span class="mono">{{ d.code }}</span>
                <strong>{{ d.name }}</strong>
              </button>
            </div>
          </div>

          <div class="field">
            <label>备注（可选）</label>
            <input v-model="ngRemark" class="input" placeholder="例如：散热片变形，已隔离" />
          </div>
        </div>
        <div class="modal-foot">
          <button class="btn" @click="ngOpen = false">取消</button>
        </div>
      </div>
    </div>
  </AppShell>
</template>

<style scoped>
.scan-layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 340px;
  gap: 16px;
  align-items: start;
}

.scan-main {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-width: 0;
}

.station-row {
  display: flex;
  align-items: center;
  gap: 22px;
  flex-wrap: wrap;
}

.station-meta {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.chip-line {
  font-size: 12px;
  color: var(--muted);
}

.step-now {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 16px;
}

.step-badge {
  font-family: var(--mono);
  font-size: 11px;
  background: var(--primary-soft);
  color: var(--primary-dark);
  padding: 2px 7px;
  border-radius: 5px;
}

.station-counters {
  margin-left: auto;
  display: flex;
  gap: 24px;
}

.cnt {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
}

.cnt span {
  font-size: 11.5px;
  color: var(--muted);
}

.cnt strong {
  font-size: 22px;
  font-variant-numeric: tabular-nums;
}

.cnt.ok strong {
  color: var(--ok);
}

.cnt.bad strong {
  color: var(--danger);
}

.order-current {
  margin-top: 12px;
  padding: 12px 14px;
  border-radius: 9px;
  background: linear-gradient(90deg, #eff6ff, #f8fafc);
  border: 1px solid #dbeafe;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
  flex-wrap: wrap;
}

.order-no {
  font-size: 15px;
  font-weight: 650;
  letter-spacing: 0.4px;
}

.order-progress {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 12.5px;
  color: var(--text-2);
}

.order-chips {
  display: flex;
  gap: 8px;
  margin-top: 12px;
  flex-wrap: wrap;
}

.order-chip {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 1px;
  padding: 6px 11px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--surface);
  cursor: pointer;
  font-size: 11.5px;
  color: var(--text-2);
  transition: all 0.15s;
}

.order-chip b {
  font-size: 12.5px;
  color: var(--text);
}

.order-chip:hover {
  border-color: var(--primary);
}

.order-chip.active {
  border-color: var(--primary);
  background: var(--primary-soft);
}

.scan-card {
  border-color: #bfdbfe;
  box-shadow: 0 6px 24px rgba(37, 99, 235, 0.09);
}

.scan-label {
  display: block;
  font-size: 12.5px;
  font-weight: 650;
  color: var(--text-2);
  margin-bottom: 8px;
}

.scan-actions {
  display: grid;
  grid-template-columns: 1.4fr 1fr;
  gap: 12px;
  margin-top: 14px;
}

.scan-btn {
  height: 58px;
  font-size: 16px;
  font-weight: 650;
  gap: 10px;
}

.key {
  font-family: var(--mono);
  font-size: 10.5px;
  padding: 2px 6px;
  border-radius: 5px;
  background: rgba(255, 255, 255, 0.22);
  border: 1px solid rgba(255, 255, 255, 0.3);
}

.scan-hint {
  margin-top: 12px;
  font-size: 12px;
  color: var(--muted);
  line-height: 1.7;
}

.feedback {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 16px 18px;
  border-radius: var(--radius);
  border: 1px solid transparent;
  box-shadow: var(--shadow-sm);
}

.feedback.ok {
  background: #f0fdf4;
  border-color: #bbf7d0;
  color: #14532d;
}

.feedback.fail {
  background: #fef2f2;
  border-color: #fecaca;
  color: #7f1d1d;
}

.feedback.info {
  background: #eff6ff;
  border-color: #bfdbfe;
  color: #1e3a8a;
}

.fb-icon {
  width: 42px;
  height: 42px;
  border-radius: 50%;
  display: grid;
  place-items: center;
  background: rgba(255, 255, 255, 0.7);
  flex: 0 0 42px;
}

.fb-icon svg {
  width: 22px;
  height: 22px;
}

.fb-title {
  font-size: 16px;
  font-weight: 700;
}

.fb-detail {
  font-size: 12.5px;
  opacity: 0.85;
  margin-top: 2px;
  word-break: break-all;
}

.pop-enter-active {
  transition: all 0.2s ease;
}

.pop-enter-from {
  opacity: 0;
  transform: translateY(-6px);
}

.last-scan .badge {
  height: 24px;
}

.scan-side {
  position: sticky;
  top: 76px;
  max-height: calc(100vh - 100px);
  display: flex;
  flex-direction: column;
}

.log-list {
  overflow-y: auto;
  padding: 6px 0;
}

.log-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 16px;
  border-bottom: 1px solid var(--surface-3);
}

.log-item:last-child {
  border-bottom: none;
}

.log-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: 0 0 8px;
}

.log-dot.pass {
  background: #22c55e;
}

.log-dot.fail {
  background: #ef4444;
}

.log-sn {
  font-size: 12.5px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.defect-cat {
  font-size: 11.5px;
  font-weight: 650;
  color: var(--muted);
  letter-spacing: 0.6px;
  margin: 6px 0 8px;
}

.defect-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(132px, 1fr));
  gap: 8px;
}

.defect-btn {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 1px;
  padding: 9px 11px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--surface);
  cursor: pointer;
  transition: all 0.15s;
}

.defect-btn span {
  font-size: 10.5px;
  color: var(--muted);
}

.defect-btn strong {
  font-size: 13px;
  color: var(--text);
}

.defect-btn:hover {
  border-color: var(--danger);
  background: #fef2f2;
}

@media (max-width: 1100px) {
  .scan-layout {
    grid-template-columns: 1fr;
  }

  .scan-side {
    position: static;
    max-height: 420px;
  }
}

@media (max-width: 640px) {
  .scan-actions {
    grid-template-columns: 1fr;
  }

  .station-counters {
    margin-left: 0;
    width: 100%;
    justify-content: flex-start;
    gap: 32px;
  }

  .cnt {
    align-items: flex-start;
  }
}
</style>
