<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import AppShell from '@/components/AppShell.vue'
import { analysisApi, mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import type { MasterBootstrap, YieldRow } from '@/types'

const by = ref<'order' | 'product'>('order')
const range = ref('30d')
const rows = ref<YieldRow[]>([])
const loading = ref(false)
const error = ref('')
const selected = ref<YieldRow | null>(null)

// 机种 / 工单下钻：报告页的主线是"先看机种整体，再落到哪一单掉链子"
const boot = ref<MasterBootstrap | null>(null)
const productCode = ref('')
const orderNo = ref('')

const summary = computed(() => {
  const list = rows.value
  if (!list.length) return { input: 0, completed: 0, fail: 0, yield: 0, fpy: 0 }
  const input = list.reduce((s, r) => s + r.inputQty, 0)
  const completed = list.reduce((s, r) => s + r.completedQty, 0)
  const fail = list.reduce((s, r) => s + r.failQty, 0)
  const yieldPct = input ? Math.round((completed * 1000) / input) / 10 : 0
  // 汇总 FPY 按投入加权，避免小单拉偏
  const fpy = list.reduce((s, r) => s + (r.firstPassYield * r.inputQty) / 100, 0)
  return { input, completed, fail, yield: yieldPct, fpy: input ? Math.round((fpy * 1000) / input) / 10 : 0 }
})

const worstStep = computed(() => {
  const map = new Map<string, { pass: number; fail: number }>()
  for (const r of rows.value) {
    for (const s of r.steps) {
      const cur = map.get(s.stepName) ?? { pass: 0, fail: 0 }
      cur.pass += s.passQty
      cur.fail += s.failQty
      map.set(s.stepName, cur)
    }
  }
  return [...map.entries()]
    .map(([name, v]) => ({
      name,
      total: v.pass + v.fail,
      yieldPercent: v.pass + v.fail ? Math.round((v.pass * 1000) / (v.pass + v.fail)) / 10 : 100,
    }))
    .filter((x) => x.total > 0)
    .sort((a, b) => a.yieldPercent - b.yieldPercent)
    .slice(0, 5)
})

const maxDefect = computed(() => Math.max(1, ...(selected.value?.topDefects ?? []).map((d) => d.count)))

function rangeParams() {
  if (range.value === 'all') return {}
  const days = range.value === '7d' ? 7 : 30
  const from = new Date()
  from.setDate(from.getDate() - days)
  return { from: from.toISOString(), to: new Date().toISOString() }
}

// 请求序号：连点筛选时只认最后一次的结果，避免先发的慢请求把后来的结果覆盖掉
let loadSeq = 0

async function load() {
  const seq = ++loadSeq
  loading.value = true
  error.value = ''
  try {
    const { from, to } = rangeParams()
    const { data } = await analysisApi.yield({
      by: by.value,
      from,
      to,
      product: productCode.value || undefined,
      order: orderNo.value.trim() || undefined,
    })
    if (seq !== loadSeq) return
    rows.value = [...data.rows].sort((a, b) => a.yieldPercent - b.yieldPercent)
    selected.value = rows.value[0] ?? null
  } catch (e) {
    if (seq !== loadSeq) return
    error.value = parseApiError(e)
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}

function switchBy(v: 'order' | 'product') {
  by.value = v
  // 按机种看的时候，工单号筛选没有意义（会把它筛成一行），清掉避免误读
  if (v === 'product') orderNo.value = ''
  load()
}

function resetFilters() {
  productCode.value = ''
  orderNo.value = ''
  range.value = '30d'
  load()
}

function tone(percent: number) {
  return percent >= 98 ? 'ok' : percent >= 95 ? 'warn' : 'bad'
}

/** 良率阈值映射到既有 badge 配色（bad 对应 badge-danger） */
function badgeClass(percent: number) {
  const t = tone(percent)
  return t === 'ok' ? 'badge-ok' : t === 'warn' ? 'badge-warn' : 'badge-danger'
}

onMounted(async () => {
  try {
    const { data } = await mesApi.bootstrap()
    boot.value = data
  } catch {
    // 机种清单拿不到不影响主报表
  }
  load()
})
</script>

<template>
  <AppShell title="工单报告" subtitle="按工单 / 机种看良品率与直通率（FPY），定位最该改善的工序与不良">
    <div v-if="error" class="alert alert-error" style="margin-bottom: 14px">{{ error }}</div>

    <section class="card" style="margin-bottom: 14px">
      <div class="card-body toolbar">
        <div class="seg">
          <button class="seg-btn" :class="{ active: by === 'order' }" @click="switchBy('order')">按工单</button>
          <button class="seg-btn" :class="{ active: by === 'product' }" @click="switchBy('product')">按机种</button>
        </div>
        <select v-model="productCode" class="select" style="width: 210px" @change="load">
          <option value="">全部机种</option>
          <option v-for="p in boot?.products ?? []" :key="p.id" :value="p.code">{{ p.code }} · {{ p.name }}</option>
        </select>
        <input
          v-model="orderNo"
          class="input"
          style="max-width: 200px"
          :disabled="by === 'product'"
          :placeholder="by === 'product' ? '按机种查看时不按工单筛' : '工单号，如 WO2026'"
          @keyup.enter="load"
        />
        <select v-model="range" class="select" style="width: 140px" @change="load">
          <option value="7d">近 7 天</option>
          <option value="30d">近 30 天</option>
          <option value="all">全部</option>
        </select>
        <button class="btn" :disabled="loading" @click="load">{{ loading ? '计算中…' : '刷新' }}</button>
        <button class="btn btn-ghost" @click="resetFilters">重置</button>
      </div>
      <div class="card-body" style="padding-top: 0">
        <span class="small muted">良品率 = 完工 ÷ 投入 · 直通率 FPY = 一次没判不良就完工 ÷ 投入（差出来的就是返修救回的）</span>
      </div>
    </section>

    <div class="kpi-row">
      <div class="mini">
        <span>投入</span><strong>{{ summary.input }}</strong><em>台</em>
      </div>
      <div class="mini">
        <span>完工</span><strong>{{ summary.completed }}</strong><em>台</em>
      </div>
      <div class="mini">
        <span>不良次数</span><strong>{{ summary.fail }}</strong><em>次</em>
      </div>
      <div class="mini" :class="tone(summary.yield)">
        <span>良品率</span><strong>{{ summary.yield }}</strong><em>%</em>
      </div>
      <div class="mini" :class="tone(summary.fpy)">
        <span>直通率 FPY</span><strong>{{ summary.fpy }}</strong><em>%</em>
      </div>
    </div>

    <div class="grid">
      <section class="card">
        <div class="card-head">
          <h3>{{ by === 'order' ? '工单良率' : '机种良率' }}</h3>
          <span class="small muted">点击一行看工序明细</span>
        </div>
        <div class="card-body" style="padding-top: 0">
          <div v-if="!rows.length && !loading" class="empty">没有数据，换个时间范围、机种或工单号试试</div>
          <table v-else class="table">
            <thead>
              <tr>
                <th>{{ by === 'order' ? '工单' : '机种' }}</th>
                <th>投入</th>
                <th>完工</th>
                <th>隔离</th>
                <th>不良</th>
                <th>良品率</th>
                <th>直通率</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="r in rows"
                :key="r.key"
                class="clickable"
                :class="{ selected: selected?.key === r.key }"
                @click="selected = r"
              >
                <td>
                  <div class="mono">{{ r.key }}</div>
                  <div class="small muted">{{ r.displayName }}</div>
                </td>
                <td>{{ r.inputQty }}</td>
                <td>{{ r.completedQty }}</td>
                <td>{{ r.holdQty }}</td>
                <td>{{ r.failQty }}</td>
                <td>
                  <span class="badge" :class="badgeClass(r.yieldPercent)">{{ r.yieldPercent }}%</span>
                </td>
                <td>
                  <span class="badge" :class="badgeClass(r.firstPassYield)">{{ r.firstPassYield }}%</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section class="card">
        <div class="card-head">
          <h3>工序良率（最差 5 道）</h3>
          <span class="small muted">整体口径，跨所选范围</span>
        </div>
        <div class="card-body">
          <div v-if="!worstStep.length" class="empty">暂无工序数据</div>
          <div v-for="s in worstStep" :key="s.name" class="hbar-row">
            <span class="hbar-label">{{ s.name }}</span>
            <div class="hbar-track">
              <div class="hbar-fill" :class="tone(s.yieldPercent)" :style="`width: ${s.yieldPercent}%`"></div>
            </div>
            <span class="hbar-value">{{ s.yieldPercent }}%</span>
          </div>
        </div>
      </section>

      <section v-if="selected" class="card span-2">
        <div class="card-head">
          <h3>不良柏拉图 · <span class="mono">{{ selected.key }}</span></h3>
          <span class="small muted">
            {{ selected.topDefects.length ? `TOP ${selected.topDefects.length} 占全部不良 ${selected.topDefects.at(-1)?.cumulativePercent}%` : '无不良记录' }}
          </span>
        </div>
        <div class="card-body">
          <div v-if="!selected.topDefects.length" class="empty">该{{ by === 'order' ? '工单' : '机种' }}没有不良记录</div>
          <div v-else class="pareto">
            <div v-for="d in selected.topDefects" :key="d.code" class="pareto-col">
              <div class="pareto-count">{{ d.count }}</div>
              <div class="pareto-bar" :style="`height: ${Math.max(6, (d.count / maxDefect) * 100)}%`">
                <span class="pareto-cum">{{ d.cumulativePercent }}%</span>
              </div>
              <div class="pareto-label">{{ d.name }}</div>
              <div class="pareto-code mono">{{ d.code }}</div>
            </div>
          </div>
          <div v-if="selected.steps.length" style="margin-top: 18px">
            <div class="small muted" style="margin-bottom: 8px">该{{ by === 'order' ? '工单' : '机种' }}各工序良率</div>
            <div v-for="s in selected.steps" :key="s.stepName" class="hbar-row">
              <span class="hbar-label">{{ s.stepName }}</span>
              <div class="hbar-track">
                <div class="hbar-fill" :class="tone(s.yieldPercent)" :style="`width: ${s.yieldPercent}%`"></div>
              </div>
              <span class="hbar-value">{{ s.yieldPercent }}%</span>
            </div>
          </div>
        </div>
      </section>
    </div>
  </AppShell>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  align-items: center;
  flex-wrap: wrap;
}
.seg {
  display: inline-flex;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
}
.seg-btn {
  background: transparent;
  border: 0;
  color: var(--muted);
  padding: 7px 16px;
  font-size: 13px;
  cursor: pointer;
}
.seg-btn.active {
  background: var(--primary);
  color: #fff;
}
.kpi-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 12px;
  margin-bottom: 14px;
}
.mini {
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
  padding: 14px 16px;
}
.mini span {
  display: block;
  font-size: 12px;
  color: var(--muted);
}
.mini strong {
  font-size: 24px;
  font-weight: 600;
  margin-right: 4px;
}
.mini em {
  font-style: normal;
  font-size: 12px;
  color: var(--muted);
}
.mini.ok strong { color: #16a34a; }
.mini.warn strong { color: #d97706; }
.mini.bad strong { color: #dc2626; }
.grid {
  display: grid;
  grid-template-columns: 1.4fr 1fr;
  gap: 14px;
}
@media (max-width: 1000px) {
  .grid { grid-template-columns: 1fr; }
}
.span-2 { grid-column: 1 / -1; }
.clickable { cursor: pointer; }
.clickable:hover { background: var(--surface-2); }
.selected { background: var(--surface-2); }
.hbar-row {
  display: grid;
  grid-template-columns: 88px 1fr 56px;
  gap: 10px;
  align-items: center;
  margin-bottom: 9px;
}
.hbar-label { font-size: 12.5px; color: var(--muted); }
.hbar-track {
  height: 8px;
  background: var(--surface-2);
  border-radius: 99px;
  overflow: hidden;
}
.hbar-fill { height: 100%; border-radius: 99px; }
.hbar-fill.ok { background: #16a34a; }
.hbar-fill.warn { background: #d97706; }
.hbar-fill.bad { background: #dc2626; }
.hbar-value { font-size: 12px; text-align: right; }
.pareto {
  display: flex;
  gap: 18px;
  align-items: flex-end;
  height: 210px;
  padding: 0 4px;
}
.pareto-col {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  height: 100%;
  justify-content: flex-end;
}
.pareto-count { font-size: 12px; color: var(--muted); margin-bottom: 4px; }
.pareto-bar {
  width: 100%;
  max-width: 64px;
  background: linear-gradient(180deg, #d97706, #b45309);
  border-radius: 6px 6px 0 0;
  display: flex;
  align-items: flex-start;
  justify-content: center;
  min-height: 22px;
}
.pareto-cum { font-size: 10.5px; color: #fff; margin-top: 3px; }
.pareto-label { font-size: 12.5px; margin-top: 8px; }
.pareto-code { font-size: 10.5px; color: var(--muted); }
</style>
