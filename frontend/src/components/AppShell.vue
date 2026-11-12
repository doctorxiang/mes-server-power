<script setup lang="ts">
import { computed, ref, onMounted, onBeforeUnmount } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { ROLE_LABELS } from '@/types'
import { formatClock } from '@/utils/format'

defineProps<{ title?: string; subtitle?: string; wide?: boolean }>()

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const now = ref(new Date())
let timer: number | undefined
onMounted(() => {
  timer = window.setInterval(() => (now.value = new Date()), 1000 * 20)
})
onBeforeUnmount(() => window.clearInterval(timer))

const pageTitle = computed(() => (route.meta.title as string) ?? '电源 MES')
const roleLabel = computed(() => ROLE_LABELS[auth.role] ?? auth.role)
const initial = computed(() => auth.displayName.slice(0, 1))

const navGroups = computed(() => [
  {
    label: '生产执行',
    items: [
      { to: '/scan', label: '扫码报工', icon: 'scan' },
      { to: '/work-orders', label: '工单管理', icon: 'orders' },
      { to: '/dashboard', label: '车间看板', icon: 'board' },
    ],
  },
  {
    label: '质量追溯',
    items: [
      { to: '/trace', label: 'SN 追溯', icon: 'trace' },
      ...(auth.canHandleQuality ? [{ to: '/repair', label: '维修站', icon: 'trace' }] : []),
    ],
  },
  {
    label: '系统',
    items: [
      { to: '/master', label: '主数据', icon: 'master' },
      ...(auth.isAdmin
        ? [
            { to: '/users', label: '账号管理', icon: 'users' },
            { to: '/audit', label: '操作审计', icon: 'audit' },
          ]
        : []),
    ],
  },
])

async function logout() {
  await auth.logout()
  router.replace('/login')
}
</script>

<template>
  <div class="app-shell">
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
          <RouterLink v-for="item in group.items" :key="item.to" :to="item.to" class="nav-item">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <template v-if="item.icon === 'scan'">
                <path d="M3 7V5a2 2 0 0 1 2-2h2M17 3h2a2 2 0 0 1 2 2v2M21 17v2a2 2 0 0 1-2 2h-2M7 21H5a2 2 0 0 1-2-2v-2" />
                <path d="M7 8v8M11 8v8M15 8v8M17 8v8" />
              </template>
              <template v-else-if="item.icon === 'orders'">
                <path d="M9 3h6l1 3H8l1-3Z" />
                <path d="M5 6h14v15H5z" />
                <path d="M9 12h6M9 16h4" />
              </template>
              <template v-else-if="item.icon === 'board'">
                <rect x="3" y="4" width="18" height="12" rx="2" />
                <path d="M8 20h8M12 16v4M7 12l3-3 2 2 4-4" />
              </template>
              <template v-else-if="item.icon === 'trace'">
                <circle cx="11" cy="11" r="7" />
                <path d="m20 20-3.5-3.5M8 11h6M11 8v6" />
              </template>
              <template v-else-if="item.icon === 'master'">
                <path d="M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3Z" />
                <path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6" />
                <path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" />
              </template>
              <template v-else-if="item.icon === 'audit'">
                <path d="M7 3h7l4 4v13a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z" />
                <path d="M13 3v5h5" />
                <path d="m9 14 1.8 1.8L14.5 12" />
              </template>
              <template v-else>
                <circle cx="9" cy="8" r="3.2" />
                <path d="M3 20c0-3.3 2.7-5.5 6-5.5s6 2.2 6 5.5" />
                <path d="M16 4.5a3 3 0 0 1 0 6M18 20c0-2.4-1-4.2-2.5-5" />
              </template>
            </svg>
            <span>{{ item.label }}</span>
          </RouterLink>
        </template>
      </nav>

      <div class="sidebar-foot">
        <span class="dot-live"></span>
        <span>线体在线 · 200 终端</span>
      </div>
    </aside>

    <div class="main">
      <header class="topbar">
        <div class="topbar-title">{{ pageTitle }}</div>
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
