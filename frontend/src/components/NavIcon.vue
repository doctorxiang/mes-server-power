<script setup lang="ts">
/**
 * 侧边栏图标集：抽成组件是因为「工单管理」现在既是可折叠分组标题（button），
 * 其它项是链接（RouterLink），图标只写一份，避免两处 v-if 链走样。
 */
defineProps<{ name?: string }>()
</script>

<template>
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
  >
    <template v-if="name === 'scan'">
      <path d="M3 7V5a2 2 0 0 1 2-2h2M17 3h2a2 2 0 0 1 2 2v2M21 17v2a2 2 0 0 1-2 2h-2M7 21H5a2 2 0 0 1-2-2v-2" />
      <path d="M7 8v8M11 8v8M15 8v8M17 8v8" />
    </template>
    <template v-else-if="name === 'orders'">
      <path d="M9 3h6l1 3H8l1-3Z" />
      <path d="M5 6h14v15H5z" />
      <path d="M9 12h6M9 16h4" />
    </template>
    <template v-else-if="name === 'board'">
      <rect x="3" y="4" width="18" height="12" rx="2" />
      <path d="M8 20h8M12 16v4M7 12l3-3 2 2 4-4" />
    </template>
    <template v-else-if="name === 'trace'">
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5M8 11h6M11 8v6" />
    </template>
    <template v-else-if="name === 'qc'">
      <path d="M6 3h9l4 4v14a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z" />
      <path d="M14 3v4h4" />
      <path d="M8.5 13.5 11 16l4.5-5" />
    </template>
    <template v-else-if="name === 'master'">
      <path d="M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3Z" />
      <path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6" />
      <path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" />
    </template>
    <template v-else-if="name === 'aps'">
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M3 10h18M8 3v4M16 3v4" />
      <path d="M7 14h4M7 17h7" />
    </template>
    <template v-else-if="name === 'analysis'">
      <path d="M4 20V10M10 20V4M16 20v-7M22 20H2" />
    </template>
    <template v-else-if="name === 'files'">
      <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z" />
      <path d="M3 11h18" />
    </template>
    <template v-else-if="name === 'audit'">
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
</template>
