<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import AppShell from '@/components/AppShell.vue'
import { mesApi, qcApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import type { MasterBootstrap, QcCandidateRow, QcInspectionRow, QcSummary } from '@/types'
import { QC_CATEGORIES } from '@/types'
import { formatTime } from '@/utils/format'

const auth = useAuthStore()
const router = useRouter()

const boot = ref<MasterBootstrap | null>(null)
const rows = ref<QcInspectionRow[]>([])
const candidates = ref<QcCandidateRow[]>([])
const summary = ref<QcSummary>({ inspected: 0, pass: 0, fail: 0, passRate: 0, orders: 0, lines: 0 })
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const message = ref<{ kind: 'ok' | 'fail'; text: string } | null>(null)

// 筛选
const filterLineId = ref(0)
const filterProduct = ref('')
const filterOrderNo = ref('')
const filterResult = ref('')
const filterFrom = ref('')
const filterTo = ref('')

// 录入
const snInput = ref('')
const category = ref<string>(QC_CATEGORIES[0])
const result = ref<'Pass' | 'Fail'>('Pass')
const defectCode = ref('')
const remark = ref('')

const canWrite = computed(() => auth.canHandleQuality)
const failDefects = computed(() => (boot.value?.defectCodes ?? []).filter((d) => d.category !== '来料'))

function queryParams() {
  return {
    lineId: filterLineId.value || undefined,
    product: filterProduct.value || undefined,
    order: filterOrderNo.value.trim() || undefined,
    result: filterResult.value || undefined,
    from: filterFrom.value || undefined,
    to: filterTo.value || undefined,
  }
}

// 连点筛选时只认最后一次请求，避免慢响应覆盖新结果
let loadSeq = 0

async function loadRecords() {
  const seq = ++loadSeq
  loading.value = true
  error.value = ''
  try {
    const { data } = await qcApi.inspections({ ...queryParams(), limit: 200 })
    if (seq !== loadSeq) return
    rows.value = data.rows
    summary.value = data.summary
  } catch (e) {
    if (seq !== loadSeq) return
    error.value = parseApiError(e)
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}

async function loadCandidates() {
  try {
    candidates.value = (
      await qcApi.candidates({
        lineId: filterLineId.value || undefined,
        product: filterProduct.value || undefined,
        order: filterOrderNo.value.trim() || undefined,
        limit: 20,
      })
    ).data
  } catch {
    // 候选列表拿不到不影响录入
  }
}

async function search() {
  await Promise.all([loadRecords(), loadCandidates()])
}

function resetFilters() {
  filterLineId.value = 0
  filterProduct.value = ''
  filterOrderNo.value = ''
  filterResult.value = ''
  filterFrom.value = ''
  filterTo.value = ''
  search()
}

function pickCandidate(row: QcCandidateRow) {
  snInput.value = row.sn
  message.value = null
}

async function submit() {
  message.value = null
  const sn = snInput.value.trim().toUpperCase()
  if (!sn) {
    message.value = { kind: 'fail', text: '请先扫描或输入已包装成品的 SN' }
    return
  }
  if (result.value === 'Fail' && !defectCode.value) {
    message.value = { kind: 'fail', text: '判不合格时必须选不良代码，否则这批抽检没有可复盘的证据' }
    return
  }

  saving.value = true
  try {
    const { data } = await qcApi.create({
      sn,
      category: category.value,
      result: result.value,
      defectCode: result.value === 'Fail' ? defectCode.value : undefined,
      remark: remark.value.trim() || undefined,
    })
    message.value = {
      kind: data.result === 'Pass' ? 'ok' : 'fail',
      text: `${data.sn} ${data.category}抽检${data.result === 'Pass' ? '合格' : `不合格（${data.defectName ?? '未记不良'}）`}，已记入抽检台账`,
    }
    snInput.value = ''
    remark.value = ''
    defectCode.value = ''
    await Promise.all([loadRecords(), loadCandidates()])
  } catch (e) {
    message.value = { kind: 'fail', text: parseApiError(e) }
  } finally {
    saving.value = false
  }
}

function gotoTrace(sn: string) {
  router.push({ path: '/trace', query: { sn } })
}

function tone(percent: number) {
  return percent >= 98 ? 'ok' : percent >= 95 ? 'warn' : 'bad'
}

onMounted(async () => {
  try {
    const { data } = await mesApi.bootstrap()
    boot.value = data
  } catch {
    // 主数据失败不挡抽检台账
  }
  await search()
})
</script>

<template>
  <AppShell title="QC 抽检" subtitle="已包装成品的出货前抽检：按批抽检留证据，不合格不报废、加严抽检并回溯同批">
    <div v-if="error" class="alert alert-error" style="margin-bottom: 14px">{{ error }}</div>

    <section class="card" style="margin-bottom: 14px">
      <div class="card-body toolbar">
        <label>
          <span class="small muted">抽检日期 从</span>
          <input v-model="filterFrom" type="date" class="input" style="width: 150px" @change="search" />
        </label>
        <label>
          <span class="small muted">到</span>
          <input v-model="filterTo" type="date" class="input" style="width: 150px" @change="search" />
        </label>
        <label>
          <span class="small muted">产线</span>
          <select v-model.number="filterLineId" class="select" style="width: 170px" @change="search">
            <option :value="0">全部产线</option>
            <option v-for="l in boot?.lines ?? []" :key="l.id" :value="l.id">{{ l.name }}</option>
          </select>
        </label>
        <label>
          <span class="small muted">机种</span>
          <select v-model="filterProduct" class="select" style="width: 200px" @change="search">
            <option value="">全部机种</option>
            <option v-for="p in boot?.products ?? []" :key="p.id" :value="p.code">{{ p.code }} · {{ p.name }}</option>
          </select>
        </label>
        <label>
          <span class="small muted">工单</span>
          <input v-model="filterOrderNo" class="input" style="width: 170px" placeholder="如 WO2026" @keyup.enter="search" />
        </label>
        <label>
          <span class="small muted">判定</span>
          <select v-model="filterResult" class="select" style="width: 120px" @change="search">
            <option value="">全部</option>
            <option value="Pass">合格</option>
            <option value="Fail">不合格</option>
          </select>
        </label>
        <button class="btn" :disabled="loading" @click="search">{{ loading ? '查询中…' : '查询' }}</button>
        <button class="btn btn-ghost" @click="resetFilters">重置</button>
      </div>
    </section>

    <div class="kpi-row">
      <div class="mini">
        <span>抽检台数</span><strong>{{ summary.inspected }}</strong><em>台</em>
      </div>
      <div class="mini ok">
        <span>合格</span><strong>{{ summary.pass }}</strong><em>台</em>
      </div>
      <div class="mini" :class="summary.fail ? 'bad' : ''">
        <span>不合格</span><strong>{{ summary.fail }}</strong><em>台</em>
      </div>
      <div class="mini" :class="tone(summary.passRate)">
        <span>抽检合格率</span><strong>{{ summary.passRate }}</strong><em>%</em>
      </div>
      <div class="mini">
        <span>覆盖工单</span><strong>{{ summary.orders }}</strong><em>张</em>
      </div>
    </div>

    <div class="qc-grid">
      <section class="card">
        <div class="card-head">
          <h3>抽检录入</h3>
          <span class="small muted">只接受已包装（末道包装工序完工）的成品</span>
        </div>
        <div class="card-body">
          <div v-if="!canWrite" class="alert" style="margin-bottom: 12px">当前账号可查看抽检台账，录入需要质检员 / 班组长 / 管理员权限。</div>
          <div class="field">
            <label>SN</label>
            <div class="row" style="gap: 8px">
              <input
                v-model="snInput"
                class="input input-lg"
                style="flex: 1"
                placeholder="扫描或输入成品 SN，回车提交"
                autocomplete="off"
                spellcheck="false"
                :disabled="!canWrite"
                @keyup.enter="submit"
              />
              <button class="btn btn-primary" style="height: 48px; min-width: 96px" :disabled="!canWrite || saving" @click="submit">
                {{ saving ? '提交中…' : '记录' }}
              </button>
            </div>
          </div>

          <div class="grid-2" style="margin-top: 12px">
            <div class="field">
              <label>抽检类别</label>
              <select v-model="category" class="select" :disabled="!canWrite">
                <option v-for="c in QC_CATEGORIES" :key="c" :value="c">{{ c }}</option>
              </select>
            </div>
            <div class="field">
              <label>判定</label>
              <div class="seg" style="width: 100%">
                <button class="seg-btn" style="flex: 1" :class="{ active: result === 'Pass' }" :disabled="!canWrite" @click="result = 'Pass'">合格</button>
                <button class="seg-btn" style="flex: 1" :class="{ active: result === 'Fail' }" :disabled="!canWrite" @click="result = 'Fail'">不合格</button>
              </div>
            </div>
          </div>

          <div v-if="result === 'Fail'" class="field" style="margin-top: 12px">
            <label>不良代码（不合格必填）</label>
            <select v-model="defectCode" class="select" :disabled="!canWrite">
              <option value="">请选择不良</option>
              <option v-for="d in failDefects" :key="d.code" :value="d.code">{{ d.code }} · {{ d.name }}（{{ d.category }}）</option>
            </select>
          </div>

          <div class="field" style="margin-top: 12px">
            <label>备注</label>
            <input v-model="remark" class="input" maxlength="256" placeholder="如：标签歪斜 / 加严抽检已通知产线" :disabled="!canWrite" />
          </div>

          <div
            v-if="message"
            class="alert"
            :class="message.kind === 'ok' ? 'alert-ok' : 'alert-error'"
            style="margin-top: 12px"
          >
            {{ message.text }}
          </div>
        </div>
      </section>

      <section class="card">
        <div class="card-head">
          <h3>待抽检成品（已包装）</h3>
          <span class="small muted">点一行填入 SN</span>
        </div>
        <div class="card-body" style="padding-top: 0">
          <div v-if="!candidates.length" class="empty">没有已包装的成品，先在包装工序完工</div>
          <table v-else class="table">
            <thead>
              <tr>
                <th>SN</th>
                <th>工单 / 机种</th>
                <th>完工时间</th>
                <th>已抽</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="c in candidates" :key="c.unitId" class="clickable" @click="pickCandidate(c)">
                <td class="mono">{{ c.sn }}</td>
                <td class="small">
                  <div class="mono">{{ c.orderNo }}</div>
                  <div class="muted">{{ c.productName }}</div>
                </td>
                <td class="small muted">{{ formatTime(c.completedAtUtc) }}</td>
                <td class="small">
                  <span v-if="c.inspectedTimes" class="badge badge-warn">{{ c.inspectedTimes }} 次</span>
                  <span v-else class="muted">—</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </div>

    <section class="card" style="margin-top: 14px">
      <div class="card-head">
        <h3>抽检台账</h3>
        <span class="small muted">点 SN 看完整工序履历 · 共 {{ summary.inspected }} 条符合条件的记录</span>
      </div>
      <div v-if="loading" class="empty">加载中…</div>
      <div v-else-if="!rows.length" class="empty">没有符合条件的抽检记录</div>
      <div v-else class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th>SN</th>
              <th>工单</th>
              <th>机种 / 产线</th>
              <th>类别</th>
              <th>判定</th>
              <th>不良</th>
              <th>抽检人</th>
              <th>抽检时间</th>
              <th>备注</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in rows" :key="r.id" class="clickable" @click="gotoTrace(r.sn)">
              <td class="mono">{{ r.sn }}</td>
              <td class="mono">{{ r.orderNo }}</td>
              <td class="small">
                <div>{{ r.productName }}</div>
                <div class="muted">{{ r.lineName }}</div>
              </td>
              <td class="small">{{ r.category }}</td>
              <td>
                <span class="badge" :class="r.result === 'Pass' ? 'badge-ok' : 'badge-danger'">
                  {{ r.result === 'Pass' ? '合格' : '不合格' }}
                </span>
              </td>
              <td>
                <span v-if="r.defectName" class="badge badge-warn">{{ r.defectCode }} · {{ r.defectName }}</span>
                <span v-else class="muted small">—</span>
              </td>
              <td class="small">{{ r.inspectorName }}</td>
              <td class="small muted">{{ formatTime(r.inspectedAtUtc) }}</td>
              <td class="small muted">{{ r.remark ?? '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </AppShell>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  align-items: flex-end;
  flex-wrap: wrap;
}

.toolbar label {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.qc-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 14px;
}

@media (max-width: 1000px) {
  .qc-grid {
    grid-template-columns: 1fr;
  }
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

.alert-ok {
  border-left: 4px solid #16a34a;
  background: rgba(22, 163, 74, 0.08);
}

.alert-error {
  border-left: 4px solid #dc2626;
}

.clickable {
  cursor: pointer;
}

.clickable:hover td {
  background: var(--primary-soft) !important;
}
</style>
