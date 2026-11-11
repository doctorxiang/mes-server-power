<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import AppShell from '@/components/AppShell.vue'
import { mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import type { TraceResponse } from '@/types'
import { REPAIR_ACTION_LABELS, REPAIR_STATUS_LABELS, STATUS_LABELS } from '@/types'
import { formatDuration, formatTime } from '@/utils/format'

const route = useRoute()
const sn = ref('')
const result = ref<TraceResponse | null>(null)
const loading = ref(false)
const error = ref('')
const searched = ref(false)
const inputRef = ref<HTMLInputElement | null>(null)

async function search() {
  const code = sn.value.trim().toUpperCase()
  if (!code) return
  loading.value = true
  error.value = ''
  result.value = null
  searched.value = true
  try {
    const { data } = await mesApi.trace(code)
    result.value = data
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    loading.value = false
  }
}

function resultLabel(r: string | null): string {
  if (!r) return '未过站'
  if (r === 'Pass') return '合格'
  if (r === 'Fail') return '不良'
  return r
}

onMounted(() => {
  const q = route.query.sn as string | undefined
  if (q) {
    sn.value = q
    search()
  } else {
    inputRef.value?.focus()
  }
})
</script>

<template>
  <AppShell title="SN 追溯" subtitle="一台一码，完整工序履历、操作人、工位与不良记录">
    <section class="card search-card">
      <div class="card-body">
        <div class="row" style="gap: 10px">
          <input
            ref="inputRef"
            v-model="sn"
            class="input input-lg"
            placeholder="扫描或输入 SN，回车查询"
            autocomplete="off"
            spellcheck="false"
            @keyup.enter="search"
          />
          <button class="btn btn-primary" style="height: 54px; min-width: 110px" :disabled="loading" @click="search">
            {{ loading ? '查询中…' : '追溯' }}
          </button>
        </div>
        <div class="small muted" style="margin-top: 8px">支持扫描枪直接扫描产品条码；也可从工单详情点击 SN 跳转。</div>
      </div>
    </section>

    <div v-if="error" class="alert alert-error" style="margin-top: 16px">{{ error }}</div>
    <div v-if="loading" class="empty">查询中…</div>

    <template v-if="result">
      <section class="card" style="margin-top: 16px">
        <div class="card-body">
          <div class="trace-head">
            <div>
              <div class="mono sn-big">{{ result.sn }}</div>
              <div class="small muted" style="margin-top: 4px">
                {{ result.productCode }} · {{ result.productName }} · {{ result.lineName }} · 工单
                <span class="mono">{{ result.orderNo }}</span>
              </div>
            </div>
            <div class="trace-stats">
              <div>
                <span>状态</span>
                <strong :class="{
                  ok: result.status === 'Completed',
                  bad: result.status === 'Hold' || result.status === 'Scrapped',
                  info: result.status === 'InProcess',
                }">{{ STATUS_LABELS[result.status] ?? result.status }}</strong>
              </div>
              <div><span>当前工序</span><strong style="font-size: 15px">{{ result.currentStepName }}</strong></div>
              <div><span>合格 / 不良</span><strong style="font-size: 15px">{{ result.passCount }} / {{ result.failCount }}</strong></div>
              <div><span>上线时间</span><strong style="font-size: 14px">{{ formatTime(result.createdAtUtc) }}</strong></div>
            </div>
          </div>
        </div>
      </section>

      <section class="card" style="margin-top: 16px">
        <div class="card-head">
          <h3>工序履历</h3>
          <span class="small muted">按工艺路线顺序</span>
        </div>
        <div class="card-body">
          <ol class="timeline">
            <li v-for="s in result.steps" :key="s.seq" :class="{
              pass: s.result === 'Pass' && !s.defectName,
              fail: s.result === 'Fail',
              pending: !s.result,
            }">
              <div class="tl-dot">
                <span v-if="s.result === 'Pass' && !s.defectName">✓</span>
                <span v-else-if="s.result === 'Fail'">!</span>
                <span v-else>{{ s.seq }}</span>
              </div>
              <div class="tl-body">
                <div class="tl-head">
                  <strong>{{ s.stepName }}</strong>
                  <span class="mono small muted">{{ s.stepCode }}</span>
                  <span class="badge" :class="{
                    'badge-ok': s.result === 'Pass' && !s.defectName,
                    'badge-danger': s.result === 'Fail',
                  }">{{ resultLabel(s.result) }}</span>
                  <span v-if="s.defectName" class="badge badge-warn">{{ s.defectName }}</span>
                </div>
                <div class="tl-meta small muted">
                  <template v-if="s.scanAtUtc">
                    {{ formatTime(s.scanAtUtc) }} · {{ s.operatorName }} · 工位 {{ s.stationCode }}
                    <template v-if="s.durationSeconds"> · 循环 {{ formatDuration(s.durationSeconds) }}</template>
                  </template>
                  <template v-else>尚未过站</template>
                </div>
                <div v-if="s.remark" class="tl-remark small">{{ s.remark }}</div>
              </div>
            </li>
          </ol>
        </div>
      </section>

      <section v-if="result.repairs.length" class="card" style="margin-top: 16px">
        <div class="card-head">
          <h3>维修记录</h3>
          <span class="small muted">共 {{ result.repairs.length }} 次，含重复维修历史</span>
        </div>
        <div class="card-body">
          <ol class="timeline">
            <li v-for="r in result.repairs" :key="r.id" :class="{ fail: r.status === 'Scrapped' }">
              <span class="tl-dot" />
              <div>
                <div class="tl-head">
                  <strong>第 {{ r.repairNo }} 次维修</strong>
                  <span class="small muted">不良工序「{{ r.sourceStepName }}」</span>
                  <span v-if="r.defectName" class="badge badge-warn">{{ r.defectName }}</span>
                  <span class="badge" :class="{
                    'badge-warn': r.status === 'InProgress',
                    'badge-ok': r.status === 'Repaired',
                    'badge-danger': r.status === 'Scrapped',
                  }">{{ REPAIR_STATUS_LABELS[r.status] }}</span>
                </div>
                <div class="tl-meta small muted">
                  措施 {{ REPAIR_ACTION_LABELS[r.action] ?? r.action }}
                  · {{ formatTime(r.startedAtUtc) }} 进站（{{ r.operatorName }}{{ r.stationCode ? ` · ${r.stationCode}` : '' }})
                  <template v-if="r.completedAtUtc">
                    · {{ formatTime(r.completedAtUtc) }} 出站（{{ r.checkoutByName }}）
                  </template>
                  <template v-else>· 尚未出站</template>
                </div>
                <div v-if="r.remark" class="tl-remark small">{{ r.remark }}</div>
              </div>
            </li>
          </ol>
        </div>
      </section>
    </template>

    <div v-else-if="searched && !loading && !error" class="empty">未找到记录</div>
  </AppShell>
</template>

<style scoped>
.search-card {
  border-color: #bfdbfe;
}

.sn-big {
  font-size: 22px;
  font-weight: 700;
  letter-spacing: 0.6px;
}

.trace-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
  flex-wrap: wrap;
}

.trace-stats {
  display: flex;
  gap: 28px;
  flex-wrap: wrap;
}

.trace-stats div {
  display: flex;
  flex-direction: column;
}

.trace-stats span {
  font-size: 11.5px;
  color: var(--muted);
}

.trace-stats strong {
  font-size: 18px;
}

.trace-stats strong.ok {
  color: var(--ok);
}

.trace-stats strong.bad {
  color: var(--danger);
}

.trace-stats strong.info {
  color: var(--primary-dark);
}

.timeline {
  list-style: none;
  margin: 0;
  padding: 0;
  position: relative;
}

.timeline::before {
  content: '';
  position: absolute;
  left: 15px;
  top: 12px;
  bottom: 12px;
  width: 2px;
  background: var(--surface-3);
}

.timeline li {
  display: flex;
  gap: 14px;
  padding: 10px 0;
  position: relative;
}

.tl-dot {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  flex: 0 0 32px;
  display: grid;
  place-items: center;
  font-size: 13px;
  font-weight: 700;
  background: var(--surface-3);
  color: var(--muted);
  border: 3px solid var(--surface);
  z-index: 1;
}

.timeline li.pass .tl-dot {
  background: var(--ok-soft);
  color: var(--ok);
}

.timeline li.fail .tl-dot {
  background: var(--danger-soft);
  color: var(--danger);
}

.tl-body {
  padding-top: 3px;
  min-width: 0;
}

.tl-head {
  display: flex;
  align-items: center;
  gap: 9px;
  flex-wrap: wrap;
}

.tl-head strong {
  font-size: 14px;
}

.tl-meta {
  margin-top: 3px;
}

.tl-remark {
  margin-top: 4px;
  padding: 5px 9px;
  background: var(--surface-2);
  border-left: 2px solid var(--border-strong);
  border-radius: 4px;
  color: var(--text-2);
}

.timeline li.pending .tl-head strong {
  color: var(--muted);
  font-weight: 500;
}
</style>
