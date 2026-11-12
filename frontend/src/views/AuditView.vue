<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import AppShell from '@/components/AppShell.vue'
import { auditApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import type { AuditDetail, AuditRow } from '@/types'
import { ROLE_LABELS } from '@/types'
import { formatTimeSec } from '@/utils/format'

const rows = ref<AuditRow[]>([])
const total = ref(0)
const page = ref(1)
const size = 50
const loading = ref(false)
const error = ref('')

const filters = ref({ range: 'today', userName: '', action: '', success: '' })
const detail = ref<AuditDetail | null>(null)
const detailLoading = ref(false)

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / size)))

const ACTION_LABELS: Record<string, string> = {
  'Auth.Login': '登录',
  'Auth.Logout': '退出登录',
  'Auth.ChangePassword': '修改密码',
  'Auth.CreateUser': '创建账号',
  'Auth.UpdateUser': '修改账号',
  'WorkOrders.Create': '新建工单',
  'WorkOrders.ChangeStatus': '工单状态变更',
  'Production.HoldHandle': '不良处理',
  'Production.RepairCheckin': '维修进站',
  'Production.RepairCheckout': '维修出站',
  'Exceptions.Create': '上报异常',
  'Exceptions.Close': '关闭异常',
}

function actionLabel(action: string | null) {
  if (!action) return '—'
  return ACTION_LABELS[action] ?? action
}

function rangeToDates(range: string): { from?: string; to?: string } {
  const now = new Date()
  const start = new Date()
  if (range === 'today') start.setHours(0, 0, 0, 0)
  else if (range === '7d') start.setDate(start.getDate() - 7)
  else if (range === '30d') start.setDate(start.getDate() - 30)
  else return {}
  return { from: start.toISOString(), to: now.toISOString() }
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const { from, to } = rangeToDates(filters.value.range)
    const { data } = await auditApi.query({
      from,
      to,
      userName: filters.value.userName.trim() || undefined,
      action: filters.value.action.trim() || undefined,
      success: filters.value.success === '' ? undefined : filters.value.success === 'true',
      page: page.value,
      size,
    })
    rows.value = data.items
    total.value = data.total
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    loading.value = false
  }
}

function search() {
  page.value = 1
  load()
}

function go(delta: number) {
  const next = page.value + delta
  if (next < 1 || next > pageCount.value) return
  page.value = next
  load()
}

async function openDetail(row: AuditRow) {
  detailLoading.value = true
  detail.value = null
  try {
    const { data } = await auditApi.detail(row.id)
    detail.value = data
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    detailLoading.value = false
  }
}

function prettyPayload(payload: string | null | undefined) {
  if (!payload) return '（无请求体）'
  try {
    return JSON.stringify(JSON.parse(payload), null, 2)
  } catch {
    return payload
  }
}

onMounted(load)
</script>

<template>
  <AppShell title="操作审计" subtitle="所有写操作留痕：谁、何时、哪台终端、做了什么、成功与否">
    <div v-if="error" class="alert alert-error" style="margin-bottom: 14px">{{ error }}</div>

    <section class="card" style="margin-bottom: 14px">
      <div class="audit-filters">
        <div class="field">
          <label>时间范围</label>
          <select v-model="filters.range" class="select" @change="search">
            <option value="today">今天</option>
            <option value="7d">近 7 天</option>
            <option value="30d">近 30 天</option>
            <option value="all">全部</option>
          </select>
        </div>
        <div class="field">
          <label>操作人</label>
          <input v-model="filters.userName" class="input" placeholder="用户名" @keyup.enter="search" />
        </div>
        <div class="field">
          <label>动作</label>
          <input v-model="filters.action" class="input" placeholder="如 WorkOrders" @keyup.enter="search" />
        </div>
        <div class="field">
          <label>结果</label>
          <select v-model="filters.success" class="select" @change="search">
            <option value="">全部</option>
            <option value="true">成功</option>
            <option value="false">失败</option>
          </select>
        </div>
        <button class="btn btn-primary" :disabled="loading" @click="search">查询</button>
      </div>
    </section>

    <section class="card">
      <div v-if="loading" class="empty">加载中…</div>
      <div v-else-if="!rows.length" class="empty">该条件下没有审计记录</div>
      <div v-else class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th style="width: 132px">时间</th>
              <th style="width: 110px">操作人</th>
              <th style="width: 110px">动作</th>
              <th>接口</th>
              <th style="width: 86px">结果</th>
              <th style="width: 74px">耗时</th>
              <th style="width: 118px">终端 IP</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in rows" :key="r.id" class="audit-row" @click="openDetail(r)">
              <td class="mono small">{{ formatTimeSec(r.createdAtUtc) }}</td>
              <td>
                <div>{{ r.userName ?? '—' }}</div>
                <div class="small muted">{{ r.role ? (ROLE_LABELS[r.role] ?? r.role) : '未登录' }}</div>
              </td>
              <td class="small">{{ actionLabel(r.action) }}</td>
              <td>
                <div class="mono small">{{ r.method }} {{ r.path }}</div>
                <div v-if="r.message" class="small" style="color: var(--danger)">{{ r.message }}</div>
              </td>
              <td>
                <span class="badge" :class="r.success ? 'badge-ok' : 'badge-danger'">{{ r.statusCode }}</span>
              </td>
              <td class="small muted mono">{{ r.durationMs }}ms</td>
              <td class="small muted mono">{{ r.clientIp ?? '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="audit-pager">
        <span class="small muted">共 {{ total }} 条 · 第 {{ page }} / {{ pageCount }} 页</span>
        <div style="display: flex; gap: 8px">
          <button class="btn btn-sm" :disabled="page <= 1 || loading" @click="go(-1)">上一页</button>
          <button class="btn btn-sm" :disabled="page >= pageCount || loading" @click="go(1)">下一页</button>
        </div>
      </div>
    </section>

    <div v-if="detail || detailLoading" class="modal-mask" @click.self="detail = null">
      <div class="modal" style="max-width: 720px">
        <div class="modal-head">
          <h3>审计详情 #{{ detail?.id ?? '' }}</h3>
          <button class="btn btn-sm btn-ghost" @click="detail = null">关闭</button>
        </div>
        <div class="modal-body">
          <div v-if="detailLoading" class="empty">加载中…</div>
          <template v-else-if="detail">
            <div class="detail-grid">
              <div><label>时间</label><div class="mono">{{ formatTimeSec(detail.createdAtUtc) }}</div></div>
              <div><label>操作人</label><div>{{ detail.userName ?? '—' }}（{{ detail.role ?? '未登录' }}）</div></div>
              <div><label>接口</label><div class="mono">{{ detail.method }} {{ detail.path }}</div></div>
              <div><label>结果</label>
                <div>
                  <span class="badge" :class="detail.success ? 'badge-ok' : 'badge-danger'">
                    {{ detail.statusCode }} {{ detail.success ? '成功' : '失败' }}
                  </span>
                </div>
              </div>
              <div><label>终端 IP</label><div class="mono">{{ detail.clientIp ?? '—' }}</div></div>
              <div><label>耗时</label><div class="mono">{{ detail.durationMs }} ms</div></div>
              <div style="grid-column: 1 / -1"><label>请求号（排查时把它发给开发）</label>
                <div class="mono" style="word-break: break-all">{{ detail.requestId ?? '—' }}</div>
              </div>
              <div v-if="detail.message" style="grid-column: 1 / -1">
                <label>失败原因</label>
                <div style="color: var(--danger)">{{ detail.message }}</div>
              </div>
            </div>
            <div class="field" style="margin-top: 14px">
              <label>请求内容（密码类字段已脱敏）</label>
              <pre class="payload">{{ prettyPayload(detail.payload) }}</pre>
            </div>
          </template>
        </div>
      </div>
    </div>
  </AppShell>
</template>

<style scoped>
.audit-filters {
  display: flex;
  gap: 12px;
  align-items: flex-end;
  flex-wrap: wrap;
}
.audit-filters .field {
  margin-bottom: 0;
  min-width: 132px;
}
.audit-row {
  cursor: pointer;
}
.audit-row:hover {
  background: var(--surface-2, rgba(255, 255, 255, 0.03));
}
.audit-pager {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 4px 2px;
}
.table-wrap {
  overflow-x: auto;
}
.detail-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px 20px;
}
.detail-grid label {
  display: block;
  font-size: 11px;
  color: var(--muted);
  margin-bottom: 2px;
}
.payload {
  margin: 0;
  padding: 12px;
  border-radius: 8px;
  background: var(--surface-2, rgba(255, 255, 255, 0.04));
  font: 12px/1.6 ui-monospace, Consolas, monospace;
  white-space: pre-wrap;
  word-break: break-all;
  max-height: 260px;
  overflow: auto;
}
</style>
