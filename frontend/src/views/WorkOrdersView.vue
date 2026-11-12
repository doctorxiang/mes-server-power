<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import AppShell from '@/components/AppShell.vue'
import { mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import type { MasterBootstrap, WorkOrderListItem } from '@/types'
import { STATUS_LABELS } from '@/types'
import { formatDate, percent } from '@/utils/format'

const router = useRouter()
const auth = useAuthStore()

const orders = ref<WorkOrderListItem[]>([])
// 全厂口径的 KPI：不随查询条件变化，避免"筛了一条产线，KPI 变成一条线的数"
const kpiOrders = ref<WorkOrderListItem[]>([])
const boot = ref<MasterBootstrap | null>(null)
const loading = ref(true)
const error = ref('')
const filter = ref<'active' | 'all' | 'done'>('active')
const keyword = ref('')
const lineId = ref(0)
const from = ref('')
const to = ref('')

const showCreate = ref(false)
const saving = ref(false)
const formError = ref('')
const form = ref({
  productId: 0,
  lineId: 0,
  planQty: 100,
  priority: 2,
  plannedStart: toLocalInput(new Date()),
  dueDate: toLocalInput(new Date(Date.now() + 3 * 86400000)),
  remark: '',
})

function toLocalInput(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

const hasFilter = computed(() => Boolean(keyword.value || lineId.value || from.value || to.value || filter.value !== 'active'))

const totals = computed(() => {
  const list = kpiOrders.value
  const active = list.filter((o) => o.status === 'Released' || o.status === 'InProduction')
  return {
    active: active.length,
    plan: active.reduce((s, o) => s + o.planQty, 0),
    completed: list.reduce((s, o) => s + o.completedQty, 0),
    // 在制单还差多少台才到计划（计划是良品数，等于需求量）
    remaining: Math.max(0, active.reduce((s, o) => s + o.planQty, 0) - active.reduce((s, o) => s + o.completedQty, 0)),
    hold: list.reduce((s, o) => s + o.holdQty, 0),
    // 报废后还没补料、卡着上不了线的单子
    shortage: list.filter((o) => o.replenishQty > 0).length,
    shortageQty: list.reduce((s, o) => s + o.replenishQty, 0),
    repairUnits: list.reduce((s, o) => s + o.repairUnits, 0),
    repairTimes: list.reduce((s, o) => s + o.repairTimes, 0),
  }
})

/** 完工进度：完工 ÷ 计划，封顶 100%（计划是良品数，完工到了就该是满格） */
function progressOf(o: WorkOrderListItem): number {
  return Math.min(100, percent(o.completedQty, o.planQty))
}

function queryParams() {
  return {
    status: filter.value === 'all' ? undefined : filter.value,
    lineId: lineId.value || undefined,
    keyword: keyword.value.trim() || undefined,
    from: from.value || undefined,
    to: to.value || undefined,
  }
}

// 连点筛选/切 Tab 时只认最后一次请求，避免慢响应把新结果盖掉
let loadSeq = 0

async function load() {
  const seq = ++loadSeq
  loading.value = true
  error.value = ''
  try {
    const { data: list } = await mesApi.workOrders(queryParams())
    if (seq !== loadSeq) return
    orders.value = list
  } catch (e) {
    if (seq !== loadSeq) return
    error.value = parseApiError(e)
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}

async function loadKpi() {
  try {
    const { data } = await mesApi.workOrders()
    kpiOrders.value = data
  } catch {
    // KPI 失败不挡住列表
  }
}

async function loadMaster() {
  try {
    const { data: master } = await mesApi.bootstrap()
    boot.value = master
    if (master.products.length && !form.value.productId) {
      form.value.productId = master.products[0].id
      form.value.lineId = master.lines[0]?.id ?? 0
    }
  } catch (e) {
    error.value = parseApiError(e)
  }
}

function selectFilter(v: 'active' | 'all' | 'done') {
  filter.value = v
  load()
}

function resetFilters() {
  filter.value = 'active'
  keyword.value = ''
  lineId.value = 0
  from.value = ''
  to.value = ''
  load()
}

async function createOrder() {
  formError.value = ''
  if (!form.value.productId || !form.value.lineId || form.value.planQty < 1) {
    formError.value = '请完整填写产品、产线与计划数量'
    return
  }
  saving.value = true
  try {
    const { data } = await mesApi.createWorkOrder({
      productId: form.value.productId,
      lineId: form.value.lineId,
      planQty: form.value.planQty,
      priority: form.value.priority,
      plannedStartUtc: new Date(form.value.plannedStart).toISOString(),
      dueDateUtc: new Date(form.value.dueDate).toISOString(),
      remark: form.value.remark || undefined,
    })
    showCreate.value = false
    await Promise.all([load(), loadKpi()])
    router.push(`/work-orders/${data.summary.id}`)
  } catch (e) {
    formError.value = parseApiError(e)
  } finally {
    saving.value = false
  }
}

function openDetail(id: number) {
  router.push(`/work-orders/${id}`)
}

onMounted(() => {
  loadMaster()
  load()
  loadKpi()
})
</script>

<template>
  <AppShell title="工单管理" subtitle="计划下达、进度跟踪与完工判定">
    <template #actions>
      <button class="btn" @click="router.push('/work-orders/report')">工单报告</button>
      <button v-if="auth.canPlan" class="btn btn-primary" @click="showCreate = true">＋ 新建工单</button>
    </template>

    <div v-if="error" class="alert alert-error" style="margin-bottom: 16px">{{ error }}</div>

    <div class="stat-grid" style="margin-bottom: 18px">
      <div class="stat accent">
        <div class="label">在制工单</div>
        <div class="value">{{ totals.active }}<small>张</small></div>
      </div>
      <div class="stat">
        <div class="label">在制计划量</div>
        <div class="value">{{ totals.plan }}<small>台</small></div>
      </div>
      <div class="stat">
        <div class="label">在制待完工</div>
        <div class="value">{{ totals.remaining }}<small>台</small></div>
      </div>
      <div class="stat ok">
        <div class="label">累计完工</div>
        <div class="value">{{ totals.completed }}<small>台</small></div>
      </div>
      <div class="stat danger">
        <div class="label">待补料</div>
        <div class="value">{{ totals.shortage }}<small>张 / {{ totals.shortageQty }} 套</small></div>
      </div>
      <div class="stat">
        <div class="label">累计返修</div>
        <div class="value">{{ totals.repairUnits }}<small>台 / {{ totals.repairTimes }} 次</small></div>
      </div>
    </div>

    <div class="card">
      <div class="toolbar">
        <div class="tabs" style="margin: 0; border: none; flex: 1">
          <button class="tab" :class="{ active: filter === 'active' }" @click="selectFilter('active')">进行中</button>
          <button class="tab" :class="{ active: filter === 'all' }" @click="selectFilter('all')">全部</button>
          <button class="tab" :class="{ active: filter === 'done' }" @click="selectFilter('done')">已完工</button>
        </div>
        <input v-model="keyword" class="input" style="max-width: 220px" placeholder="工单号 / 机型" @keyup.enter="load" />
      </div>

      <div class="filter-row">
        <label>
          <span class="small muted">产线</span>
          <select v-model.number="lineId" class="select" style="width: 170px" @change="load">
            <option :value="0">全部产线</option>
            <option v-for="l in boot?.lines ?? []" :key="l.id" :value="l.id">{{ l.name }}</option>
          </select>
        </label>
        <label>
          <span class="small muted">计划开工 从</span>
          <input v-model="from" type="date" class="input" style="width: 150px" @change="load" />
        </label>
        <label>
          <span class="small muted">到</span>
          <input v-model="to" type="date" class="input" style="width: 150px" @change="load" />
        </label>
        <button class="btn" @click="load">查询</button>
        <button class="btn btn-ghost" :disabled="!hasFilter" @click="resetFilters">重置</button>
        <span class="small muted">{{ loading ? '查询中…' : `共 ${orders.length} 张工单` }}</span>
      </div>

      <div v-if="loading" class="empty">加载中…</div>
      <div v-else-if="!orders.length" class="empty">没有符合条件的工单，换个日期或产线试试</div>
      <div v-else class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th>工单号</th>
              <th>产品</th>
              <th>产线</th>
              <th style="width: 190px">进度（完工 ÷ 计划）</th>
              <th>计划 / 发料 / 投入 / 完工 <span class="muted" style="font-weight: 400">（报废看详情）</span></th>
              <th>返修（台 / 次）</th>
              <th>状态</th>
              <th>开工 / 交期</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="o in orders" :key="o.id" class="clickable" @click="openDetail(o.id)">
              <td>
                <div class="mono" style="font-weight: 600">{{ o.orderNo }}</div>
                <div class="small" v-if="o.priority === 1" style="color: var(--danger)">急件</div>
              </td>
              <td>
                <div>{{ o.productName }}</div>
                <div class="small muted mono">{{ o.productCode }}</div>
              </td>
              <td class="small">{{ o.lineName }}</td>
              <td>
                <div class="row between small" style="margin-bottom: 5px">
                  <span class="muted">{{ progressOf(o) }}%</span>
                  <span v-if="o.replenishQty" class="badge badge-danger">需补料 {{ o.replenishQty }}</span>
                  <span v-else-if="o.holdQty" class="badge badge-warn">隔离 {{ o.holdQty }}</span>
                </div>
                <div class="progress"><span :style="{ width: `${progressOf(o)}%` }"></span></div>
              </td>
              <td class="small mono">
                {{ o.planQty }} / {{ o.issuedQty }} / {{ o.inputQty }} / {{ o.completedQty }}
              </td>
              <td class="small mono" :class="{ 'rep-warn': o.repairTimes > 0 }">
                {{ o.repairUnits }} / {{ o.repairTimes }}
              </td>
              <td>
                <span class="badge" :class="{
                  'badge-blue': o.status === 'InProduction' || o.status === 'Released',
                  'badge-ok': o.status === 'Completed',
                  'badge-warn': o.status === 'Draft',
                }">{{ STATUS_LABELS[o.status] ?? o.status }}</span>
              </td>
              <td class="small">
                <div>{{ formatDate(o.plannedStartUtc) }}</div>
                <div class="muted">交期 {{ formatDate(o.dueDateUtc) }}</div>
              </td>
              <td><span class="small muted">详情 →</span></td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- 新建工单 -->
    <div v-if="showCreate" class="modal-mask" @click.self="showCreate = false">
      <div class="modal">
        <div class="modal-head">
          <h3>新建工单</h3>
          <button class="btn btn-sm btn-ghost" @click="showCreate = false">关闭</button>
        </div>
        <div class="modal-body">
          <div v-if="formError" class="alert alert-error">{{ formError }}</div>

          <div class="field">
            <label>产品</label>
            <select v-model.number="form.productId" class="select">
              <option v-for="p in boot?.products ?? []" :key="p.id" :value="p.id">
                {{ p.code }} · {{ p.name }}
              </option>
            </select>
          </div>

          <div class="grid-2">
            <div class="field">
              <label>产线</label>
              <select v-model.number="form.lineId" class="select">
                <option v-for="l in boot?.lines ?? []" :key="l.id" :value="l.id">{{ l.name }}</option>
              </select>
            </div>
            <div class="field">
              <label>计划数量（台）</label>
              <input v-model.number="form.planQty" type="number" min="1" class="input" />
            </div>
          </div>

          <div class="grid-2">
            <div class="field">
              <label>优先级</label>
              <select v-model.number="form.priority" class="select">
                <option :value="1">急件</option>
                <option :value="2">正常</option>
                <option :value="3">低</option>
              </select>
            </div>
            <div class="field">
              <label>计划开工</label>
              <input v-model="form.plannedStart" type="datetime-local" class="input" />
            </div>
          </div>

          <div class="field">
            <label>交期</label>
            <input v-model="form.dueDate" type="datetime-local" class="input" />
          </div>

          <div class="field">
            <label>备注</label>
            <input v-model="form.remark" class="input" placeholder="例如：客户加急 / 试产单" />
          </div>
        </div>
        <div class="modal-foot">
          <button class="btn" @click="showCreate = false">取消</button>
          <button class="btn btn-primary" :disabled="saving" @click="createOrder">{{ saving ? '创建中…' : '创建并下发' }}</button>
        </div>
      </div>
    </div>
  </AppShell>
</template>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 6px 14px 0;
  border-bottom: 1px solid var(--border);
  flex-wrap: wrap;
}

/* 查询条件行：日期 / 产线都在这里，和状态 Tab 分开，避免挤成一条看不懂的横条 */
.filter-row {
  display: flex;
  align-items: flex-end;
  gap: 12px;
  flex-wrap: wrap;
  padding: 12px 14px;
  background: var(--surface-2);
  border-bottom: 1px solid var(--border);
}

.filter-row label {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.rep-warn {
  color: #d97706;
  font-weight: 700;
}

.table-wrap {
  overflow-x: auto;
}

.clickable {
  cursor: pointer;
}

.clickable:hover td {
  background: var(--primary-soft) !important;
}
</style>
