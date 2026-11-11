<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import * as signalR from '@microsoft/signalr'
import { mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { readSession } from '@/api/session'
import type { Dashboard, ExceptionRow, MasterBootstrap, RecentLog } from '@/types'
import { EXCEPTION_TYPES } from '@/types'
import { formatTime, percent } from '@/utils/format'

const auth = useAuthStore()
const data = ref<Dashboard | null>(null)
const boot = ref<MasterBootstrap | null>(null)
const exceptions = ref<ExceptionRow[]>([])
const error = ref('')
const lineId = ref<number>(0)
const live = ref(false)
const stream = ref<RecentLog[]>([])
const clock = ref(new Date())

let pollTimer: number | undefined
let clockTimer: number | undefined
let hub: signalR.HubConnection | null = null

const lineOptions = computed(() => [{ id: 0, name: '全厂' }, ...(boot.value?.lines ?? [])])

const maxHourly = computed(() => {
  const hours = data.value?.hourly ?? []
  return Math.max(1, ...hours.map((h) => h.pass + h.fail))
})

const wipMax = computed(() => Math.max(1, ...(data.value?.wipByStep ?? []).map((w) => w.wipQty)))

const yieldTone = computed(() => {
  const fpy = data.value?.fpy ?? 0
  return fpy >= 98 ? 'ok' : fpy >= 95 ? 'warn' : 'bad'
})

const attainment = computed(() => percent(data.value?.completedQty ?? 0, data.value?.planQty ?? 0))

async function load() {
  try {
    const { data: d } = await mesApi.dashboard(lineId.value || undefined)
    data.value = d
    if (!stream.value.length) stream.value = d.recent
    error.value = ''
  } catch (e) {
    error.value = parseApiError(e)
  }
}

async function loadExtras() {
  try {
    const [{ data: ex }, { data: master }] = await Promise.all([
      mesApi.exceptions('Open', lineId.value || undefined),
      boot.value ? Promise.resolve({ data: boot.value }) : mesApi.bootstrap(),
    ])
    exceptions.value = ex
    boot.value = master
  } catch {
    // 静默
  }
}

async function connectHub() {
  try {
    hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/board', { accessTokenFactory: () => readSession().accessToken })
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(signalR.LogLevel.None)
      .build()

    hub.on('scan', (evt: { sn: string; stepName: string; result: string; operatorName: string; stationCode: string; atUtc: string }) => {
      const item: RecentLog = {
        id: Date.now(),
        sn: evt.sn,
        stepName: evt.stepName,
        result: evt.result,
        defectName: null,
        operatorName: evt.operatorName,
        stationCode: evt.stationCode,
        createdAtUtc: evt.atUtc,
      }
      stream.value = [item, ...stream.value].slice(0, 9)
    })

    hub.onreconnected(() => (live.value = true))
    hub.onclose(() => (live.value = false))

    await hub.start()
    live.value = true
  } catch {
    live.value = false
  }
}

function onLineChange() {
  load()
  loadExtras()
}

function clockText(d: Date) {
  return d.toLocaleTimeString('zh-CN', { hour12: false })
}

onMounted(async () => {
  await Promise.all([load(), loadExtras()])
  connectHub()
  pollTimer = window.setInterval(load, 8000)
  clockTimer = window.setInterval(() => (clock.value = new Date()), 1000)
})

onBeforeUnmount(() => {
  if (pollTimer) window.clearInterval(pollTimer)
  if (clockTimer) window.clearInterval(clockTimer)
  hub?.stop()
})
</script>

<template>
  <div class="board">
    <header class="board-head">
      <div class="bh-left">
        <div class="brand-logo" style="width: 38px; height: 38px">MES</div>
        <div>
          <div class="bh-title">服务器电源 · 车间实时看板</div>
          <div class="bh-sub">
            {{ data?.date ?? '—' }} · {{ data?.shift ?? '—' }}
            <span class="live-dot" :class="{ on: live }"></span>
            {{ live ? '实时推送中' : '轮询刷新' }}
          </div>
        </div>
      </div>

      <div class="bh-right">
        <select v-model.number="lineId" class="board-select" @change="onLineChange">
          <option v-for="l in lineOptions" :key="l.id" :value="l.id">{{ l.name }}</option>
        </select>
        <div class="clock mono">{{ clockText(clock) }}</div>
        <RouterLink to="/work-orders" class="board-link">返回系统</RouterLink>
      </div>
    </header>

    <div v-if="error" class="board-error">{{ error }}</div>

    <div class="board-kpis">
      <div class="kpi">
        <span>计划产量</span>
        <strong>{{ data?.planQty ?? 0 }}</strong>
        <em>台</em>
      </div>
      <div class="kpi">
        <span>今日投入</span>
        <strong>{{ data?.inputQty ?? 0 }}</strong>
        <em>台</em>
      </div>
      <div class="kpi green">
        <span>今日完工</span>
        <strong>{{ data?.completedQty ?? 0 }}</strong>
        <em>台</em>
      </div>
      <div class="kpi" :class="yieldTone">
        <span>直通率 FPY</span>
        <strong>{{ data?.fpy ?? 0 }}</strong>
        <em>%</em>
      </div>
      <div class="kpi red">
        <span>不良隔离</span>
        <strong>{{ data?.holdQty ?? 0 }}</strong>
        <em>台</em>
      </div>
      <div class="kpi amber">
        <span>开放异常</span>
        <strong>{{ data?.openExceptions ?? 0 }}</strong>
        <em>项</em>
      </div>
    </div>

    <div class="board-main">
      <section class="panel span-8">
        <div class="panel-head">
          <h3>小时产出</h3>
          <div class="legend">
            <span><i class="dot ok"></i>合格</span>
            <span><i class="dot bad"></i>不良</span>
          </div>
        </div>
        <div class="chart">
          <div v-for="h in data?.hourly ?? []" :key="h.hour" class="bar-col">
            <div class="bars">
              <div class="bar ok" :style="{ height: `${((h.pass + h.fail) / maxHourly) * 100}%` }">
                <span class="bar-fail" :style="{ height: `${h.pass + h.fail ? (h.fail / (h.pass + h.fail)) * 100 : 0}%` }"></span>
              </div>
            </div>
            <div class="bar-label">{{ h.hour.slice(0, 2) }}</div>
          </div>
          <div v-if="!data?.hourly.length" class="panel-empty">今日暂无产出</div>
        </div>
        <div class="chart-foot">
          <span>达成率</span>
          <div class="progress"><span :style="{ width: `${Math.min(100, attainment)}%` }"></span></div>
          <strong>{{ attainment }}%</strong>
        </div>
      </section>

      <section class="panel span-4">
        <div class="panel-head"><h3>在制分布 WIP</h3><span class="muted-sm">共 {{ data?.wipByStep.reduce((s, w) => s + w.wipQty, 0) ?? 0 }} 台</span></div>
        <div class="wip-list">
          <div v-for="w in data?.wipByStep ?? []" :key="w.stepCode" class="wip-row">
            <div class="wip-name">{{ w.stepName }}</div>
            <div class="wip-bar"><span :style="{ width: `${(w.wipQty / wipMax) * 100}%` }"></span></div>
            <div class="wip-qty">{{ w.wipQty }}</div>
          </div>
          <div v-if="!data?.wipByStep.length" class="panel-empty">无在制</div>
        </div>
      </section>

      <section class="panel span-4">
        <div class="panel-head"><h3>TOP 不良</h3><span class="muted-sm">今日</span></div>
        <div class="defect-list">
          <div v-for="(d, i) in data?.topDefects ?? []" :key="d.code" class="defect-row">
            <span class="rank">{{ i + 1 }}</span>
            <div style="flex: 1; min-width: 0">
              <div class="d-name">{{ d.name }}</div>
              <div class="d-cat">{{ d.category }} · {{ d.code }}</div>
            </div>
            <strong>{{ d.qty }}</strong>
          </div>
          <div v-if="!data?.topDefects.length" class="panel-empty">今日无不良记录</div>
        </div>
      </section>

      <section class="panel span-4">
        <div class="panel-head"><h3>产线对比</h3><span class="muted-sm">计划 / 完工 / FPY</span></div>
        <div class="line-list">
          <div v-for="l in data?.lines ?? []" :key="l.lineId" class="line-row">
            <div class="row between">
              <strong>{{ l.lineName }}</strong>
              <span class="badge-dark">{{ l.fpy }}%</span>
            </div>
            <div class="line-nums">
              <span>计划 {{ l.planQty }}</span>
              <span>完工 {{ l.completedQty }}</span>
              <span :style="{ color: l.holdQty ? '#f87171' : 'inherit' }">不良 {{ l.holdQty }}</span>
            </div>
            <div class="progress dark"><span :style="{ width: `${Math.min(100, percent(l.completedQty, l.planQty))}%` }"></span></div>
          </div>
        </div>
      </section>

      <section class="panel span-4">
        <div class="panel-head"><h3>实时过站</h3><span class="muted-sm">最近 9 条</span></div>
        <div class="stream">
          <div v-for="s in stream" :key="s.id" class="stream-row">
            <span class="dot" :class="s.result === 'Pass' ? 'ok' : 'bad'"></span>
            <span class="mono s-sn">{{ s.sn }}</span>
            <span class="s-step">{{ s.stepName }}</span>
            <span class="s-time">{{ formatTime(s.createdAtUtc).slice(6) }}</span>
          </div>
          <div v-if="!stream.length" class="panel-empty">等待扫码数据…</div>
        </div>
      </section>

      <section class="panel span-12">
        <div class="panel-head"><h3>现场异常</h3><span class="muted-sm">开放 {{ exceptions.length }} 项</span></div>
        <div class="exception-strip">
          <div v-for="e in exceptions" :key="e.id" class="ex-item">
            <span class="ex-type">{{ EXCEPTION_TYPES[e.type] ?? e.type }}</span>
            <span class="ex-desc">{{ e.description }}</span>
            <span class="ex-meta">{{ e.lineName }} · {{ e.reportedByName }} · {{ formatTime(e.reportedAtUtc) }}</span>
          </div>
          <div v-if="!exceptions.length" class="panel-empty">现场无异常</div>
        </div>
      </section>
    </div>

    <footer class="board-foot">
      <span>{{ auth.displayName }} · 电源一线生产看板</span>
      <span>数据每 8 秒刷新 · 扫码事件实时推送</span>
    </footer>
  </div>
</template>

<style scoped>
.board {
  min-height: 100vh;
  background:
    radial-gradient(1200px 600px at 10% -10%, rgba(37, 99, 235, 0.18), transparent 55%),
    radial-gradient(900px 500px at 95% 5%, rgba(8, 145, 178, 0.14), transparent 50%),
    #070d18;
  color: #e2e8f0;
  padding: 18px 22px 14px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.board-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}

.bh-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.bh-title {
  font-size: 19px;
  font-weight: 700;
  letter-spacing: 0.4px;
  color: #f8fafc;
}

.bh-sub {
  font-size: 12px;
  color: #7c8ba1;
  display: flex;
  align-items: center;
  gap: 6px;
}

.live-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #64748b;
  margin-left: 6px;
}

.live-dot.on {
  background: #22c55e;
  box-shadow: 0 0 0 4px rgba(34, 197, 94, 0.15);
}

.bh-right {
  display: flex;
  align-items: center;
  gap: 12px;
}

.board-select {
  height: 32px;
  padding: 0 10px;
  border-radius: 7px;
  background: rgba(148, 163, 184, 0.12);
  border: 1px solid rgba(148, 163, 184, 0.22);
  color: #e2e8f0;
  font-size: 12.5px;
  outline: none;
}

.clock {
  font-size: 20px;
  font-weight: 600;
  color: #f8fafc;
  font-variant-numeric: tabular-nums;
  letter-spacing: 1px;
}

.board-link {
  font-size: 12px;
  color: #94a3b8;
  border: 1px solid rgba(148, 163, 184, 0.25);
  padding: 5px 10px;
  border-radius: 7px;
}

.board-link:hover {
  color: #e2e8f0;
  border-color: rgba(148, 163, 184, 0.5);
}

.board-error {
  background: rgba(185, 28, 28, 0.18);
  border: 1px solid rgba(248, 113, 113, 0.35);
  color: #fecaca;
  padding: 9px 14px;
  border-radius: 8px;
  font-size: 12.5px;
}

.board-kpis {
  display: grid;
  grid-template-columns: repeat(6, 1fr);
  gap: 12px;
}

.kpi {
  background: linear-gradient(180deg, rgba(30, 41, 59, 0.85), rgba(15, 23, 42, 0.85));
  border: 1px solid rgba(148, 163, 184, 0.14);
  border-radius: 11px;
  padding: 12px 16px;
  position: relative;
  overflow: hidden;
}

.kpi span {
  font-size: 11.5px;
  color: #94a3b8;
  letter-spacing: 0.4px;
}

.kpi strong {
  display: inline-block;
  font-size: 34px;
  font-weight: 720;
  line-height: 1.15;
  margin-top: 4px;
  color: #f1f5f9;
  font-variant-numeric: tabular-nums;
  letter-spacing: -0.5px;
}

.kpi em {
  font-style: normal;
  font-size: 11px;
  color: #64748b;
  margin-left: 4px;
}

.kpi.green strong {
  color: #4ade80;
}

.kpi.red strong {
  color: #f87171;
}

.kpi.amber strong {
  color: #fbbf24;
}

.kpi.ok strong {
  color: #4ade80;
}

.kpi.warn strong {
  color: #fbbf24;
}

.kpi.bad strong {
  color: #f87171;
}

.board-main {
  display: grid;
  grid-template-columns: repeat(12, 1fr);
  gap: 12px;
  flex: 1;
}

.panel {
  background: linear-gradient(180deg, rgba(23, 33, 50, 0.9), rgba(12, 19, 32, 0.9));
  border: 1px solid rgba(148, 163, 184, 0.14);
  border-radius: 11px;
  padding: 12px 16px 14px;
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.span-4 {
  grid-column: span 4;
}

.span-8 {
  grid-column: span 8;
}

.span-12 {
  grid-column: span 12;
}

.panel-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 10px;
}

.panel-head h3 {
  font-size: 13.5px;
  font-weight: 650;
  color: #e2e8f0;
  letter-spacing: 0.3px;
}

.muted-sm {
  font-size: 11px;
  color: #64748b;
}

.legend {
  display: flex;
  gap: 12px;
  font-size: 11px;
  color: #94a3b8;
}

.dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  margin-right: 5px;
}

.dot.ok {
  background: #22c55e;
}

.dot.bad {
  background: #ef4444;
}

.chart {
  flex: 1;
  display: flex;
  align-items: flex-end;
  gap: 8px;
  min-height: 150px;
  padding-top: 6px;
}

.bar-col {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  height: 100%;
  justify-content: flex-end;
}

.bars {
  width: 100%;
  height: 100%;
  display: flex;
  align-items: flex-end;
  justify-content: center;
}

.bar {
  position: relative;
  width: 68%;
  max-width: 40px;
  border-radius: 5px 5px 0 0;
  background: linear-gradient(180deg, #38bdf8, #2563eb);
  min-height: 3px;
  transition: height 0.4s;
}

.bar-fail {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  background: #ef4444;
  border-radius: 5px 5px 0 0;
}

.bar-label {
  font-size: 10px;
  color: #64748b;
  font-family: var(--mono);
}

.chart-foot {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 12px;
  font-size: 11.5px;
  color: #94a3b8;
}

.chart-foot .progress {
  flex: 1;
  background: rgba(148, 163, 184, 0.15);
}

.chart-foot strong {
  color: #4ade80;
}

.wip-list {
  display: flex;
  flex-direction: column;
  gap: 9px;
  overflow-y: auto;
}

.wip-row {
  display: grid;
  grid-template-columns: 74px 1fr 34px;
  align-items: center;
  gap: 10px;
}

.wip-name {
  font-size: 12px;
  color: #cbd5e1;
}

.wip-bar {
  height: 7px;
  background: rgba(148, 163, 184, 0.13);
  border-radius: 999px;
  overflow: hidden;
}

.wip-bar span {
  display: block;
  height: 100%;
  background: linear-gradient(90deg, #06b6d4, #3b82f6);
  border-radius: 999px;
}

.wip-qty {
  font-size: 12px;
  color: #e2e8f0;
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.defect-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.defect-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.rank {
  width: 20px;
  height: 20px;
  display: grid;
  place-items: center;
  border-radius: 6px;
  background: rgba(239, 68, 68, 0.16);
  color: #fca5a5;
  font-size: 11px;
  font-weight: 700;
}

.d-name {
  font-size: 12.5px;
  color: #e2e8f0;
}

.d-cat {
  font-size: 10.5px;
  color: #64748b;
}

.defect-row strong {
  color: #f87171;
  font-size: 15px;
  font-variant-numeric: tabular-nums;
}

.line-list {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.line-row strong {
  font-size: 12.5px;
  color: #e2e8f0;
  font-weight: 600;
}

.badge-dark {
  font-size: 11px;
  background: rgba(74, 222, 128, 0.14);
  color: #4ade80;
  padding: 2px 7px;
  border-radius: 999px;
}

.line-nums {
  display: flex;
  gap: 12px;
  font-size: 10.5px;
  color: #94a3b8;
  margin: 5px 0 6px;
}

.progress.dark {
  background: rgba(148, 163, 184, 0.14);
}

.stream {
  display: flex;
  flex-direction: column;
  gap: 7px;
  overflow: hidden;
}

.stream-row {
  display: grid;
  grid-template-columns: 14px 1fr 62px 42px;
  align-items: center;
  gap: 4px;
  font-size: 11.5px;
  color: #cbd5e1;
}

.s-sn {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11.5px;
}

.s-step {
  color: #94a3b8;
  font-size: 10.5px;
}

.s-time {
  color: #64748b;
  text-align: right;
  font-size: 10.5px;
}

.exception-strip {
  display: flex;
  gap: 10px;
  overflow-x: auto;
}

.ex-item {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 8px 12px;
  border-radius: 8px;
  background: rgba(180, 83, 9, 0.12);
  border: 1px solid rgba(251, 191, 36, 0.22);
  font-size: 11.5px;
  color: #fcd34d;
  max-width: 520px;
}

.ex-type {
  background: rgba(251, 191, 36, 0.18);
  padding: 1px 7px;
  border-radius: 5px;
  font-weight: 600;
  white-space: nowrap;
}

.ex-desc {
  color: #e2e8f0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ex-meta {
  color: #94a3b8;
  white-space: nowrap;
  font-size: 10.5px;
}

.panel-empty {
  flex: 1;
  display: grid;
  place-items: center;
  color: #475569;
  font-size: 12px;
  min-height: 70px;
}

.board-foot {
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  color: #475569;
}

@media (max-width: 1280px) {
  .board-kpis {
    grid-template-columns: repeat(3, 1fr);
  }

  .span-8,
  .span-4 {
    grid-column: span 6;
  }

  .board-main .span-12 {
    grid-column: span 12;
  }
}

@media (max-width: 760px) {
  .board-kpis {
    grid-template-columns: repeat(2, 1fr);
  }

  .span-8,
  .span-4 {
    grid-column: span 12;
  }

  .clock {
    font-size: 16px;
  }
}
</style>
