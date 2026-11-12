<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import NavIcon from '@/components/NavIcon.vue'
import { useAuthStore } from '@/stores/auth'
import { ROLE_LABELS } from '@/types'
import { formatClock } from '@/utils/format'

defineProps<{ title?: string; subtitle?: string; wide?: boolean }>()

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const now = ref(new Date())
let timer: number | undefined

/* ---------------- 侧边栏三态：展开 / 收起成图标条 / 关闭 ---------------- */
type SidebarMode = 'full' | 'icon' | 'hidden'
const SIDEBAR_KEY = 'mes.sidebarMode'
const BRANCH_KEY = 'mes.navClosed'

function loadMode(): SidebarMode {
  const v = localStorage.getItem(SIDEBAR_KEY)
  return v === 'icon' || v === 'hidden' ? v : 'full'
}

const sidebarMode = ref<SidebarMode>(loadMode())

function setMode(m: SidebarMode) {
  sidebarMode.value = m
  localStorage.setItem(SIDEBAR_KEY, m)
}

/** 收起按钮：隐藏 → 展开；展开 ↔ 图标条 */
function toggleCollapse() {
  setMode(sidebarMode.value === 'full' ? 'icon' : 'full')
}

/* ---------------- 二级菜单展开 / 收起（工单管理 → 工单列表 / 工单报告） ---------------- */
function loadClosed(): string[] {
  try {
    const raw = localStorage.getItem(BRANCH_KEY)
    const arr = raw ? JSON.parse(raw) : []
    return Array.isArray(arr) ? arr.filter((x): x is string => typeof x === 'string') : []
  } catch {
    return []
  }
}

/** 存的是「被手动收起的分组」，默认展开，只有点过收起的分组才在里面 */
const closedBranches = ref<string[]>(loadClosed())

function persistClosed() {
  localStorage.setItem(BRANCH_KEY, JSON.stringify(closedBranches.value))
}

const pageTitle = computed(() => (route.meta.title as string) ?? '电源 MES')
const roleLabel = computed(() => ROLE_LABELS[auth.role] ?? auth.role)
const initial = computed(() => auth.displayName.slice(0, 1))

interface NavItem {
  to: string
  label: string
  icon?: string
  children?: { to: string; label: string }[]
}

const navGroups = computed<{ label: string; items: NavItem[] }[]>(() => [
  {
    label: '生产执行',
    items: [
      { to: '/scan', label: '扫码报工', icon: 'scan' },
      {
        to: '/work-orders',
        label: '工单管理',
        icon: 'orders',
        // 工单报告（原良率分析）收进工单管理做子菜单：单子的进度和良率本来就该一起看
        children: [
          { to: '/work-orders', label: '工单列表' },
          { to: '/work-orders/report', label: '工单报告' },
        ],
      },
      { to: '/dashboard', label: '车间看板', icon: 'board' },
      ...(auth.role !== 'Operator' ? [{ to: '/aps', label: 'APS 排程', icon: 'aps' }] : []),
    ],
  },
  {
    label: '质量追溯',
    items: [
      { to: '/trace', label: 'SN 追溯', icon: 'trace' },
      ...(auth.canHandleQuality ? [{ to: '/repair', label: '维修站', icon: 'trace' }] : []),
      ...(auth.canHandleQuality || auth.role === 'Viewer'
        ? [{ to: '/qc', label: 'QC 抽检', icon: 'qc' }]
        : []),
    ],
  },
  {
    label: '系统',
    items: [
      { to: '/master', label: '主数据', icon: 'master' },
      { to: '/files', label: '工艺文件', icon: 'files' },
      ...(auth.isAdmin
        ? [
            { to: '/users', label: '账号管理', icon: 'users' },
            { to: '/audit', label: '操作审计', icon: 'audit' },
          ]
        : []),
    ],
  },
])

/** 当前路由落在这个分组的路径里（含工单详情这类子路径） */
function inBranch(item: NavItem): boolean {
  return route.path === item.to || route.path.startsWith(`${item.to}/`)
}

function isBranchOpen(item: NavItem): boolean {
  return !closedBranches.value.includes(item.to)
}

function openBranch(item: NavItem) {
  if (!closedBranches.value.includes(item.to)) return
  closedBranches.value = closedBranches.value.filter((k) => k !== item.to)
  persistClosed()
}

function toggleBranch(item: NavItem) {
  // 图标条模式下点分组 = 先把侧边栏展开，再保证这个分组是开的（否则点了像没反应）
  if (sidebarMode.value !== 'full') {
    setMode('full')
    openBranch(item)
    return
  }
  if (isBranchOpen(item)) {
    closedBranches.value = [...closedBranches.value, item.to]
    persistClosed()
  } else {
    openBranch(item)
  }
}

// 每个页面各挂一个 AppShell，进入时按当前路由校正一次；
// 落在「工单报告 / 工单详情」这类二级路径时强制展开分组，
// 但停在工单列表（分组首页）时保留用户上次的收起状态。
function syncActiveBranch() {
  const hit = navGroups.value
    .flatMap((g) => g.items)
    .filter((it) => it.children?.length && route.path !== it.to && inBranch(it))
  if (!hit.length) return
  const next = closedBranches.value.filter((k) => !hit.some((it) => it.to === k))
  if (next.length !== closedBranches.value.length) {
    closedBranches.value = next
    persistClosed()
  }
}

syncActiveBranch()
watch(() => route.path, syncActiveBranch)

function onKeydown(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'b') {
    e.preventDefault()
    toggleCollapse()
  }
}

onMounted(() => {
  timer = window.setInterval(() => (now.value = new Date()), 1000 * 20)
  window.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  window.clearInterval(timer)
  window.removeEventListener('keydown', onKeydown)
})

async function logout() {
  await auth.logout()
  router.replace('/login')
}
</script>

<template>
  <div class="app-shell" :class="`mode-${sidebarMode}`">
    <aside class="sidebar">
      <div class="sidebar-brand">
        <div class="brand-logo">MES</div>
        <div class="brand-text">
          <strong>电源制造执行</strong>
          <span>SERVER POWER</span>
        </div>
      </div>

      <nav class="nav">
        <template v-for="group in navGroups" :key="group.label">
          <div class="nav-group">{{ group.label }}</div>

          <template v-for="item in group.items" :key="item.to">
            <!-- 带子项：分组标题负责收起/展开，子项负责导航 -->
            <div
              v-if="item.children"
              class="nav-branch"
              :class="{ open: isBranchOpen(item), current: inBranch(item) }"
            >
              <button
                type="button"
                class="nav-item nav-parent"
                :title="item.label"
                :aria-expanded="isBranchOpen(item)"
                @click="toggleBranch(item)"
              >
                <NavIcon :name="item.icon" />
                <span class="nav-label">{{ item.label }}</span>
                <svg
                  class="nav-caret"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                >
                  <path d="m9 6 6 6-6 6" />
                </svg>
              </button>

              <div class="nav-sub-wrap">
                <div class="nav-sub">
                  <RouterLink
                    v-for="child in item.children"
                    :key="child.to"
                    :to="child.to"
                    class="nav-sub-item"
                    :title="child.label"
                  >
                    <span class="nav-sub-dot"></span>
                    {{ child.label }}
                  </RouterLink>
                </div>
              </div>
            </div>

            <RouterLink v-else :to="item.to" class="nav-item" :title="item.label">
              <NavIcon :name="item.icon" />
              <span class="nav-label">{{ item.label }}</span>
            </RouterLink>
          </template>
        </template>
      </nav>

      <div class="sidebar-foot">
        <span class="dot-live"></span>
        <span>线体在线 · 200 终端</span>
      </div>
    </aside>

    <div class="main">
      <header class="topbar">
        <div class="topbar-left">
          <div class="layout-switch">
            <button
              v-if="sidebarMode === 'hidden'"
              class="btn-icon"
              title="显示侧边栏（Ctrl+B）"
              @click="setMode('full')"
            >
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <rect x="3" y="4" width="18" height="16" rx="2" />
                <path d="M9 4v16" />
                <path d="m14 10 2 2-2 2" />
              </svg>
            </button>
            <template v-else>
              <button
                class="btn-icon"
                :title="sidebarMode === 'full' ? '收起侧边栏（Ctrl+B）' : '展开侧边栏（Ctrl+B）'"
                @click="toggleCollapse"
              >
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                  <rect x="3" y="4" width="18" height="16" rx="2" />
                  <path d="M9 4v16" />
                  <path v-if="sidebarMode === 'full'" d="m16 9-3 3 3 3" />
                  <path v-else d="m13 9 3 3-3 3" />
                </svg>
              </button>
              <button class="btn-icon" title="关闭侧边栏" @click="setMode('hidden')">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                  <rect x="3" y="4" width="18" height="16" rx="2" />
                  <path d="M9 4v16" />
                  <path d="m14 9 5 6m0-6-5 6" />
                </svg>
              </button>
            </template>
          </div>
          <div class="topbar-title">{{ pageTitle }}</div>
        </div>

        <div class="topbar-meta">
          <span class="mono">{{ formatClock(now) }}</span>
          <div class="user-chip">
            <div class="avatar">{{ initial }}</div>
            <div style="line-height: 1.2">
              <div style="font-size: 12.5px; font-weight: 600; color: var(--text)">{{ auth.displayName }}</div>
              <div style="font-size: 11px; color: var(--muted)">{{ roleLabel }}</div>
            </div>
          </div>
          <button class="btn btn-sm btn-ghost" @click="logout">退出</button>
        </div>
      </header>

      <main class="page" :style="wide ? 'max-width:none' : ''">
        <div v-if="title" class="page-header">
          <div>
            <h1>{{ title }}</h1>
            <p v-if="subtitle">{{ subtitle }}</p>
          </div>
          <slot name="actions" />
        </div>
        <slot />
      </main>
    </div>
  </div>
</template>
