<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import AppShell from '@/components/AppShell.vue'
import { apsApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import type { ApsResult } from '@/types'

const result = ref<ApsResult | null>(null)
const includeDraft = ref(false)
const loading = ref(false)
const error = ref('')

/** 甘特条：把每单的开工/完工映射成时间轴百分比 */
const timeline = computed(() => {
  const rows = result.value?.rows ?? []
  if (!rows.length) return { rows: [], min: 0, max: 1, ticks: [] as { label: string; left: number }[] }

  const starts = rows.map((r) => new Date(r.startLocal.replace(' ', 'T')).getTime())
  const ends = rows.map((r) => new Date(r.endLocal.replace(' ', 'T')).getTime())
  const min = Math.min(...starts)
  // 至少给 1 小时跨度，避免单工单时条占比 100% 看起来像满负荷 bug
  const max = Math.max(...ends, min + 3600_000)
  const span = max - min

  const bars = rows.map((r) => {
    const s = new Date(r.startLocal.replace(' ', 'T')).getTime()
    const e = new Date(r.endLocal.replace(' ', 'T')).getTime()
    return {
      ...r,
      left: ((s - min) / span) * 100,
      width: Math.max(1.2, ((e - s) / span) * 100),
    }
  })

  const ticks = Array.from({ length: 5 }, (_, i) => {
    const t = new Date(min + (span * i) / 4)
    const label = `${String(t.getMonth() + 1).padStart(2, '0')}-${String(t.getDate()).padStart(2, '0')} ${String(t.getHours()).padStart(2, '0')}:00`
    return { label, left: i * 25 }
  })

  return { rows: bars, min, max, ticks }
})

async function run() {
  loading.value = true
  error.value = ''
  try {
    const { data } = await apsApi.run(includeDraft.value)
    result.value = data
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    loading.value = false
  }
}

onMounted(run)
</script>

<template>
  <AppShell title="APS 排程" subtitle="按瓶颈工序节拍 + 良率折损做有限产能正排，给出每张工单的开工/完工时间">
    <template #actions>
      <div class="row" style="gap: 12px; align-items: center">
        <label class="small muted" style="display: flex; gap: 6px; align-items: center">
          <input v-model="includeDraft" type="checkbox" />
          含草稿工单
        </label>
        <button class="btn btn-primary" :disabled="loading" @click="run">
          {{ loading ? '排程中…' : '重新排程' }}
        </button>
      </div>
    </template>

    <div v-if="error" class="alert alert-error" style="margin-bottom: 14px">{{ error }}</div>

    <div v-if="result" class="kpi-row">
      <div class="mini"><span>计划工单</span><strong>{{ result.plannedOrders }}</strong><em>张</em></div>
      <div class="mini"><span>产线总负荷</span><strong>{{ result.totalHours }}</strong><em>小时</em></div>
      <div class="mini"><span>不能按期</span><strong>{{ result.rows.filter((r) => !r.onTime).length }}</strong><em>张</em></div>
      <div class="mini muted-block"><span>排程时间</span><strong style="font-size: 16px">{{ result.generatedAtLocal }}</strong></div>
    </div>

    <section v-if="result?.bottleneckNotes.length" class="card" style="margin-bottom: 14px">
      <div class="card-body">
        <div class="small muted" style="margin-bottom: 6px">瓶颈提示</div>
        <ul class="notes">
          <li v-for="n in result.bottleneckNotes" :key="n">{{ n }}</li>
        </ul>
      </div>
    </section>

    <section v-if="result && timeline.rows.length" class="card" style="margin-bottom: 14px">
      <div class="card-head">
        <h3>排程甘特</h3>
        <span class="small muted">条长 = 该工单占用产线的时长 · 红色 = 超出交期</span>
      </div>
      <div class="card-body">
        <div class="gantt">
          <div class="gantt-row" v-for="r in timeline.rows" :key="r.orderId">
            <div class="gantt-label">
              <div class="mono">{{ r.orderNo }}</div>
              <div class="small muted">{{ r.lineName }}</div>
            </div>
            <div class="gantt-track">
              <div
                class="gantt-bar"
                :class="{ late: !r.onTime }"
                :style="`left: ${r.left}%; width: ${r.width}%`"
                :title="`${r.orderNo} ${r.startLocal} → ${r.endLocal}（${r.loadHours}h）`"
              >
                <span>{{ r.qty }} 台</span>
              </div>
            </div>
          </div>
          <div class="gantt-axis">
            <span v-for="t in timeline.ticks" :key="t.label" :style="`left: ${t.left}%`">{{ t.label }}</span>
          </div>
        </div>
      </div>
    </section>

    <section v-if="result" class="card">
      <div class="card-head">
        <h3>排程明细</h3>
        <span class="small muted">时产能 = 3600 ÷ 瓶颈节拍 × 工位数 × (1 - 良率折损)</span>
      </div>
      <div class="card-body" style="padding-top: 0">
        <table v-if="result.rows.length" class="table">
          <thead>
            <tr>
              <th>工单</th>
              <th>机种</th>
              <th>产线</th>
              <th>数量</th>
              <th>瓶颈工序</th>
              <th>节拍</th>
              <th>时产能</th>
              <th>开工</th>
              <th>完工</th>
              <th>交期</th>
              <th>状态</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in result.rows" :key="r.orderId">
              <td class="mono">{{ r.orderNo }}</td>
              <td>{{ r.productName }}</td>
              <td>{{ r.lineName }}</td>
              <td>{{ r.qty }}</td>
              <td>{{ r.bottleneckStep }}</td>
              <td class="mono">{{ r.bottleneckTaktSeconds }}s</td>
              <td class="mono">{{ r.unitsPerHour }}</td>
              <td class="small">{{ r.startLocal }}</td>
              <td class="small">{{ r.endLocal }}</td>
              <td class="small muted">{{ r.dueDateLocal }}</td>
              <td>
                <span class="badge" :class="r.onTime ? 'badge-ok' : 'badge-danger'">
                  {{ r.onTime ? '可交付' : '逾期' }}
                </span>
              </td>
            </tr>
          </tbody>
        </table>
        <div v-else class="empty">当前没有可排程的工单（需状态为已下发）</div>
      </div>
    </section>
  </AppShell>
</template>

<style scoped>
.kpi-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
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
.notes {
  margin: 0;
  padding-left: 18px;
  font-size: 13px;
  line-height: 1.8;
}
.gantt {
  position: relative;
  padding-bottom: 30px;
}
.gantt-row {
  display: grid;
  grid-template-columns: 130px 1fr;
  gap: 12px;
  align-items: center;
  margin-bottom: 8px;
}
.gantt-label {
  text-align: right;
  font-size: 12.5px;
}
.gantt-track {
  position: relative;
  height: 26px;
  background: var(--surface-2);
  border-radius: 6px;
}
.gantt-bar {
  position: absolute;
  top: 3px;
  height: 20px;
  background: #2563eb;
  border-radius: 5px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #fff;
  font-size: 11px;
  overflow: hidden;
  white-space: nowrap;
}
.gantt-bar.late {
  background: #dc2626;
}
.gantt-axis {
  position: relative;
  height: 18px;
  margin-left: 142px;
  border-top: 1px solid var(--border);
}
.gantt-axis span {
  position: absolute;
  transform: translateX(-50%);
  font-size: 10.5px;
  color: var(--muted);
  top: 3px;
}
</style>
