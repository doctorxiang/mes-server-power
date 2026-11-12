<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AppShell from '@/components/AppShell.vue'
import { mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import type { WorkOrderDetail } from '@/types'
import { STATUS_LABELS } from '@/types'
import { formatDate, formatTime, percent } from '@/utils/format'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const id = Number(route.params.id)
const detail = ref<WorkOrderDetail | null>(null)
const loading = ref(true)
const error = ref('')
const actionMsg = ref('')

// 补料：报废一台就补一台，补完才能再上线，最终完工数才能补回计划

const showReplenish = ref(false)
const replenishQty = ref(0)
const replenishReason = ref('')
const replenishing = ref(false)

/** 完工进度：完工 ÷ 计划（计划是良品数，达到就是 100%） */
const progress = computed(() => Math.min(100, percent(summary.value?.completedQty ?? 0, summary.value?.planQty ?? 1)))

/** 剩余可用料 = 发料 − 已上线 */
const remainingMaterial = computed(() => {
  const s = summary.value
  if (!s) return 0
  return Math.max(0, s.issuedQty - s.inputQty)
})

function openReplenish() {
  replenishQty.value = Math.max(summary.value?.replenishQty ?? 0, 1)
  replenishReason.value = summary.value?.replenishQty ? '报废补料' : '提前备料'
  showReplenish.value = true
}

/** 已补料量 = 累计发料 − 计划 */
const replenished = computed(() => Math.max(0, (summary.value?.issuedQty ?? 0) - (summary.value?.planQty ?? 0)))

/** 报废率：报废 ÷ 投入，千分之几属正常 */
const scrapRate = computed(() => {
  const s = summary.value
  if (!s || !s.inputQty) return '0.00'
  return ((s.scrapQty * 100) / s.inputQty).toFixed(2)
})

/** 返修率：进过维修站的台数 ÷ 投入（修两次的只算一台，否则会重复计） */
const repairRate = computed(() => {
  const s = summary.value
  if (!s || !s.inputQty) return '0.00'
  return ((s.repairUnits * 100) / s.inputQty).toFixed(1)
})

async function submitReplenish() {
  if (!replenishQty.value || replenishQty.value < 1) return
  replenishing.value = true
  try {
    const { data } = await mesApi.replenishWorkOrder(id, replenishQty.value, replenishReason.value || undefined)
    actionMsg.value = `已补料 ${replenishQty.value} 套，发料总量 ${data.issuedQty} 套`
    showReplenish.value = false
    await load()
  } catch (e) {
    actionMsg.value = parseApiError(e)
  } finally {
    replenishing.value = false
  }
}

let timer: number | undefined

const summary = computed(() => detail.value?.summary)
const activeStepSeq = computed(() => {
  const steps = detail.value?.steps ?? []
  const withWip = steps.filter((s) => s.wipQty > 0).sort((a, b) => a.seq - b.seq)
  return withWip[0]?.seq ?? 0
})

async function load() {
  try {
    const { data } = await mesApi.workOrder(id)
    detail.value = data
    error.value = ''
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    loading.value = false
  }
}

async function changeStatus(action: 'close' | 'reopen') {
  try {
    await mesApi.changeWorkOrderStatus(id, action)
    actionMsg.value = action === 'close' ? '工单已关单' : '工单已重新打开'
    await load()
  } catch (e) {
    actionMsg.value = parseApiError(e)
  }
}

function gotoScan() {
  router.push('/scan')
}

function gotoTrace(sn: string) {
  router.push({ path: '/trace', query: { sn } })
}

onMounted(() => {
  load()
  timer = window.setInterval(load, 15000)
})

onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
})
</script>

<template>
  <AppShell :title="summary ? `工单 ${summary.orderNo}` : '工单详情'" subtitle="工序进度、在制分布与 SN 记录" wide>
    <template #actions>
      <div class="row">
        <button class="btn" @click="router.push('/work-orders')">返回列表</button>
        <button class="btn btn-primary" @click="gotoScan">去扫码报工</button>
      </div>
    </template>

    <div v-if="error" class="alert alert-error" style="margin-bottom: 16px">{{ error }}</div>
    <div v-if="loading" class="empty">加载中…</div>
    <div v-else-if="actionMsg" class="alert alert-ok" style="margin-bottom: 14px">{{ actionMsg }}</div>

    <template v-if="summary">
      <section class="card" style="margin-bottom: 16px">
        <div class="card-body order-head">
          <div class="oh-left">
            <div class="row" style="gap: 10px">
              <span class="badge" :class="{
                'badge-blue': summary.status === 'InProduction' || summary.status === 'Released',
                'badge-ok': summary.status === 'Completed',
                'badge-warn': summary.status === 'Draft',
              }">{{ STATUS_LABELS[summary.status] ?? summary.status }}</span>
              <span v-if="summary.priority === 1" class="badge badge-danger">急件</span>
            </div>
            <h2>{{ summary.productName }}</h2>
            <div class="small muted">
              <span class="mono">{{ summary.productCode }}</span> · {{ summary.lineName }} ·
              交期 {{ formatDate(summary.dueDateUtc) }}
            </div>
          </div>

          <div class="oh-stats">
            <div><span>计划（良品）</span><strong>{{ summary.planQty }}</strong></div>
            <div><span>发料（计划 + 补料）</span><strong>{{ summary.issuedQty }}</strong></div>
            <div>
              <span>已上线</span>
              <strong>{{ summary.inputQty }}</strong>
            </div>
            <div><span>完工</span><strong style="color: var(--ok)">{{ summary.completedQty }}</strong></div>
            <div>
              <span>报废 <em class="small muted" style="font-style: normal">{{ scrapRate }}%</em></span>
              <strong style="color: var(--danger)">{{ summary.scrapQty }}</strong>
            </div>
            <div><span>补料</span><strong>{{ replenished }}</strong></div>
            <div title="返修台数按 SN 去重，返修次数含同一台的重复维修">
              <span>返修 <em class="small muted" style="font-style: normal">{{ repairRate }}%</em></span>
              <strong style="color: var(--warn, #d97706)">{{ summary.repairUnits }}<small class="small muted"> 台 / {{ summary.repairTimes }} 次</small></strong>
            </div>
            <div><span>不良隔离</span><strong style="color: var(--warn, #d97706)">{{ summary.holdQty }}</strong></div>
            <div><span>直通率 FPY</span><strong>{{ detail?.fpy ?? 0 }}%</strong></div>
          </div>

          <div class="oh-actions">
            <button
              v-if="(auth.canPlan || auth.canHandleQuality) && summary.status !== 'Closed'"
              class="btn btn-sm"
              :class="{ 'btn-primary': summary.replenishQty > 0 }"
              @click="openReplenish"
            >
              {{ summary.replenishQty > 0 ? `补料（缺 ${summary.replenishQty}）` : '补料' }}
            </button>
            <button v-if="auth.canPlan && summary.status !== 'Closed'" class="btn btn-sm" @click="changeStatus('close')">关单</button>
            <button v-if="auth.canPlan && summary.status === 'Completed'" class="btn btn-sm" @click="changeStatus('reopen')">重新打开</button>
          </div>
        </div>
        <div class="overall">
          <div class="progress" style="height: 8px">
            <span :style="{ width: `${progress}%` }"></span>
          </div>
          <div class="row between small muted" style="margin-top: 6px">
            <span>完工进度 {{ progress }}%（完工 ÷ 计划，报废后补料，完工到计划即 100%）</span>
            <span>
              {{ summary.completedQty }} / {{ summary.planQty }} 台 · 剩余可用料 {{ remainingMaterial }} 套
            </span>
          </div>
        </div>
      </section>

      <section class="card" style="margin-bottom: 16px">
        <div class="card-head">
          <h3>工序进度</h3>
          <span class="small muted">按工艺路线顺序 · 数字为该工序累计合格 / 不良</span>
        </div>
        <div class="card-body">
          <div class="flow">
            <div
              v-for="(s, idx) in detail?.steps ?? []"
              :key="s.stepId"
              class="flow-step"
              :class="{ current: s.seq === activeStepSeq, inspection: s.isInspection }"
            >
              <div class="fs-head">
                <span class="fs-seq">{{ s.seq }}</span>
                <span class="fs-name">{{ s.stepName }}</span>
              </div>
              <div class="fs-nums">
                <span class="ok">{{ s.passQty }}</span>
                <span class="sep">/</span>
                <span class="bad">{{ s.failQty }}</span>
              </div>
              <div class="fs-wip" v-if="s.wipQty">在制 {{ s.wipQty }}</div>
              <div class="fs-wip muted" v-else>—</div>
              <span v-if="idx < (detail?.steps.length ?? 0) - 1" class="fs-arrow">›</span>
            </div>
          </div>
        </div>
      </section>

      <section v-if="detail?.scrapUnits?.length" class="card" style="margin-bottom: 16px">
        <div class="card-head">
          <h3>报废明细（{{ detail.scrapUnits.length }} 台）</h3>
          <span class="small muted">报废率 {{ scrapRate }}% · 报废一台补一台，补料 {{ replenished }} 套</span>
        </div>
        <div class="table-wrap">
          <table class="table">
            <thead>
              <tr>
                <th>SN</th>
                <th>判定工序</th>
                <th>不良代码</th>
                <th>报废时间</th>
                <th>原因</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="sc in detail.scrapUnits" :key="sc.sn" class="clickable" @click="gotoTrace(sc.sn)">
                <td class="mono">{{ sc.sn }}</td>
                <td class="small">{{ sc.stepName }}</td>
                <td><span class="badge badge-danger">{{ sc.defectName }}</span></td>
                <td class="small muted">{{ sc.scrappedAtUtc ? formatTime(sc.scrappedAtUtc) : '—' }}</td>
                <td class="small muted">{{ sc.reason }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section class="card">
        <div class="card-head">
          <h3>最近 SN（{{ detail?.recentUnits.length ?? 0 }}）</h3>
          <span class="small muted">点击 SN 查看完整追溯</span>
        </div>
        <div v-if="!detail?.recentUnits.length" class="empty">该工单尚未开始生产</div>
        <div v-else class="table-wrap">
          <table class="table">
            <thead>
              <tr>
                <th>SN</th>
                <th>当前工序</th>
                <th>状态</th>
                <th>上线时间</th>
                <th>最后过站</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="u in detail.recentUnits" :key="u.id" class="clickable" @click="gotoTrace(u.sn)">
                <td class="mono">{{ u.sn }}</td>
                <td class="small">{{ u.status === 'Completed' ? '已完工' : u.currentStepName ?? '—' }}</td>
                <td>
                  <span class="badge" :class="{
                    'badge-blue': u.status === 'InProcess',
                    'badge-ok': u.status === 'Completed',
                    'badge-danger': u.status === 'Hold' || u.status === 'Scrapped',
                  }">{{ STATUS_LABELS[u.status] ?? u.status }}</span>
                </td>
                <td class="small muted">{{ formatTime(u.createdAtUtc) }}</td>
                <td class="small muted">{{ formatTime(u.lastScanAtUtc) }}</td>
                <td class="small muted">追溯 →</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </template>

    <!-- 补料：报废一台补一台 -->
    <div v-if="showReplenish" class="modal-mask" @click.self="showReplenish = false">
      <div class="modal" style="max-width: 440px">
        <div class="modal-head">
          <h3>补料</h3>
          <button class="btn btn-sm btn-ghost" @click="showReplenish = false">关闭</button>
        </div>
        <div class="modal-body">
          <p class="small muted" style="margin-top: 0">
            工单 {{ summary?.orderNo }}：计划 {{ summary?.planQty }} 台，已发 {{ summary?.issuedQty }} 套，
            已上线 {{ summary?.inputQty }}，已报废 {{ summary?.scrapQty }} 台（报废率 {{ scrapRate }}%）。
          </p>
          <p v-if="summary?.replenishQty" class="small" style="color: var(--danger); margin-top: 0">
            报废 {{ summary.scrapQty }} 台，其中 {{ summary.replenishQty }} 套还没补 —— 不补料产线无法继续上线，完工也补不到计划数量。
          </p>
          <div class="field">
            <label>补料数量（套）</label>
            <input v-model.number="replenishQty" type="number" min="1" class="input" />
          </div>
          <div class="field">
            <label>原因</label>
            <input v-model="replenishReason" class="input" placeholder="如：测试判报废补料 / 来料不良 / 提前备料" />
          </div>
          <div class="row" style="justify-content: flex-end; gap: 8px; margin-top: 14px">
            <button class="btn" @click="showReplenish = false">取消</button>
            <button class="btn btn-primary" :disabled="replenishing" @click="submitReplenish">
              {{ replenishing ? '提交中…' : '确认补料' }}
            </button>
          </div>
        </div>
      </div>
    </div>
  </AppShell>
</template>

<style scoped>
/* ---------------- 详情页放大一档 ----------------
   全局字号是按列表页的密度定的（13px 表格 / 14px 标题），详情页是「盯着看」的场景：
   铺满整页宽度 + 数字放大，隔着半米也能读。只在本页覆盖，不动全局。 */
:deep(.table) {
  font-size: 14px;
}

:deep(.table th) {
  font-size: 12.5px;
  padding: 12px 18px;
}

:deep(.table td) {
  padding: 13px 18px;
}

:deep(.small) {
  font-size: 12.5px;
}

:deep(.card-head h3) {
  font-size: 15.5px;
}

:deep(.card-head) {
  padding: 14px 18px;
}

.order-head {
  display: flex;
  align-items: flex-start;
  gap: 26px;
  flex-wrap: wrap;
  padding: 18px 20px;
}

.oh-left {
  min-width: 260px;
}

.oh-left h2 {
  font-size: 26px;
  margin: 10px 0 5px;
  letter-spacing: -0.4px;
}

.oh-stats {
  display: flex;
  gap: 30px;
  margin-left: auto;
  flex: 1 1 auto;
  justify-content: space-between;
  flex-wrap: wrap;
}

.oh-stats div {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.oh-stats span {
  font-size: 12.5px;
  color: var(--muted);
  white-space: nowrap;
}

.oh-stats strong {
  font-size: 27px;
  line-height: 1.15;
  font-variant-numeric: tabular-nums;
}

.overall {
  padding: 14px 20px 16px;
  border-top: 1px solid var(--border);
}

.flow {
  display: flex;
  gap: 10px;
  overflow-x: auto;
  padding-bottom: 4px;
}

.flow-step {
  position: relative;
  min-width: 138px;
  flex: 1;
  padding: 13px 14px;
  border: 1px solid var(--border);
  border-radius: 9px;
  background: var(--surface-2);
}

.flow-step.inspection {
  background: #f5f3ff;
  border-color: #ddd6fe;
}

.flow-step.current {
  border-color: var(--primary);
  background: var(--primary-soft);
  box-shadow: 0 4px 14px rgba(37, 99, 235, 0.12);
}

.fs-head {
  display: flex;
  align-items: center;
  gap: 6px;
}

.fs-seq {
  font-family: var(--mono);
  font-size: 10.5px;
  color: var(--muted);
}

.fs-name {
  font-size: 14.5px;
  font-weight: 600;
}

.fs-nums {
  margin-top: 8px;
  font-size: 19px;
  font-weight: 680;
  font-variant-numeric: tabular-nums;
}

.fs-nums .ok {
  color: var(--ok);
}

.fs-nums .bad {
  color: var(--danger);
}

.fs-nums .sep {
  color: var(--border-strong);
  margin: 0 2px;
  font-weight: 400;
}

.fs-wip {
  font-size: 12px;
  color: var(--text-2);
  margin-top: 3px;
}

.fs-arrow {
  position: absolute;
  right: -9px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--border-strong);
  font-size: 18px;
  z-index: 1;
}

.clickable {
  cursor: pointer;
}

.clickable:hover td {
  background: var(--primary-soft) !important;
}
</style>
