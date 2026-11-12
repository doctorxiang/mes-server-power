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
const boot = ref<MasterBootstrap | null>(null)
const loading = ref(true)
const error = ref('')
const filter = ref<'all' | 'active' | 'done'>('active')
const keyword = ref('')

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

const filtered = computed(() => {
  let list = orders.value
  if (filter.value === 'active') list = list.filter((o) => o.status === 'Released' || o.status === 'InProduction')
  if (filter.value === 'done') list = list.filter((o) => o.status === 'Completed' || o.status === 'Closed')
  const kw = keyword.value.trim().toLowerCase()
  if (kw) list = list.filter((o) => `${o.orderNo} ${o.productCode} ${o.productName}`.toLowerCase().includes(kw))
  return list
})

const totals = computed(() => {
  const active = orders.value.filter((o) => o.status === 'Released' || o.status === 'InProduction')
  return {
    active: active.length,
    plan: active.reduce((s, o) => s + o.planQty, 0),
    completed: orders.value.reduce((s, o) => s + o.completedQty, 0),
    // 在制单还差多少台才到计划（计划是良品数，等于需求量）
    remaining: Math.max(0, active.reduce((s, o) => s + o.planQty, 0) - active.reduce((s, o) => s + o.completedQty, 0)),
    hold: orders.value.reduce((s, o) => s + o.holdQty, 0),
    // 报废后还没补料、卡着上不了线的单子
    shortage: orders.value.filter((o) => o.replenishQty > 0).length,
    shortageQty: orders.value.reduce((s, o) => s + o.replenishQty, 0),
  }
})

/** 完工进度：完工 ÷ 计划，封顶 100%（计划是良品数，完工到了就该是满格） */
function progressOf(o: WorkOrderListItem): number {
  return Math.min(100, percent(o.completedQty, o.planQty))
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const [{ data: list }, { data: master }] = await Promise.all([mesApi.workOrders(), mesApi.bootstrap()])
    orders.value = list
    boot.value = master
    if (master.products.length && !form.value.productId) {
      form.value.productId = master.products[0].id
      form.value.lineId = master.lines[0]?.id ?? 0
    }
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    loading.value = false
  }
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
    await load()
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

onMounted(load)
</script>

<template>
  <AppShell title="工单管理" subtitle="计划下达、进度跟踪与完工判定">
    <template #actions>
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
    </div>

    <div class="card">
      <div class="toolbar">
        <div class="tabs" style="margin: 0; border: none; flex: 1">
          <button class="tab" :class="{ active: filter === 'active' }" @click="filter = 'active'">进行中</button>
          <button class="tab" :class="{ active: filter === 'all' }" @click="filter = 'all'">全部</button>
          <button class="tab" :class="{ active: filter === 'done' }" @click="filter = 'done'">已完工</button>
        </div>
        <input v-model="keyword" class="input" style="max-width: 240px" placeholder="搜索工单号 / 机型" />
      </div>

      <div v-if="loading" class="empty">加载中…</div>
      <div v-else-if="!filtered.length" class="empty">没有符合条件的工单</div>
      <div v-else class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th>工单号</th>
              <th>产品</th>
              <th>产线</th>
              <th style="width: 190px">进度（完工 ÷ 计划）</th>
              <th>计划 / 发料 / 投入 / 完工 <span class="muted" style="font-weight: 400">（报废看详情）</span></th>
              <th>状态</th>
              <th>交期</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="o in filtered" :key="o.id" class="clickable" @click="openDetail(o.id)">
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
              <td>
                <span class="badge" :class="{
                  'badge-blue': o.status === 'InProduction' || o.status === 'Released',
                  'badge-ok': o.status === 'Completed',
                  'badge-warn': o.status === 'Draft',
                }">{{ STATUS_LABELS[o.status] ?? o.status }}</span>
              </td>
              <td class="small">{{ formatDate(o.dueDateUtc) }}</td>
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
