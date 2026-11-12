import { ref } from 'vue'
import { defineStore } from 'pinia'
import type { RouteLocationNormalizedLoaded } from 'vue-router'

/**
 * 页签（多标签导航）：仿 Vben Admin 的 Tab 条。
 * 每个 AppShell 实例只挂在一个页面上，所以状态必须放 Pinia，
 * 再落到 localStorage，刷新后页签还在。
 */
export interface PageTab {
  path: string
  title: string
  pinned: boolean
}

const KEY = 'mes.pageTabs'
const HOME = '/work-orders'

function load(): PageTab[] {
  try {
    const raw = localStorage.getItem(KEY)
    const arr = raw ? JSON.parse(raw) : []
    if (!Array.isArray(arr)) return []
    return arr
      .filter((t) => t && typeof t.path === 'string' && typeof t.title === 'string')
      .map((t) => ({ path: t.path as string, title: t.title as string, pinned: Boolean(t.pinned) }))
  } catch {
    return []
  }
}

export const useTabsStore = defineStore('tabs', () => {
  const tabs = ref<PageTab[]>(load())
  // 置顶的「首页」页签：从别的页一路关过来也总有落脚点
  if (!tabs.value.some((t) => t.path === HOME)) {
    tabs.value.unshift({ path: HOME, title: '工单列表', pinned: true })
  }
  const active = ref('')

  function persist() {
    localStorage.setItem(KEY, JSON.stringify(tabs.value))
  }

  /** 路由变化时同步页签：有就激活，没有就新增 */
  function visit(route: RouteLocationNormalizedLoaded) {
    if (route.meta?.public) return
    const base = (route.meta.title as string) ?? route.path
    // 参数路由各占一个页签：工单详情 #12 / #15 分开开
    const id = route.params.id
    const title = id ? `${base} #${id}` : base
    active.value = route.path

    const found = tabs.value.find((t) => t.path === route.path)
    if (found) {
      found.title = title
    } else {
      tabs.value.push({ path: route.path, title, pinned: false })
    }
    persist()
  }

  /** 关闭一个页签，若关的是当前页，返回该去哪（右侧优先，否则左侧，否则首页） */
  function close(path: string): string | undefined {
    const idx = tabs.value.findIndex((t) => t.path === path)
    if (idx < 0 || tabs.value[idx].pinned) return
    const wasActive = active.value === path
    tabs.value.splice(idx, 1)
    persist()
    if (!wasActive) return
    return (tabs.value[idx] ?? tabs.value[idx - 1])?.path ?? HOME
  }

  function togglePin(path: string) {
    const tab = tabs.value.find((t) => t.path === path)
    if (!tab) return
    tab.pinned = !tab.pinned
    // 置顶的排前面，未置顶的保持原先后打开的顺序
    tabs.value = [...tabs.value.filter((t) => t.pinned), ...tabs.value.filter((t) => !t.pinned)]
    persist()
  }

  /** 关闭全部：只留置顶的；当前页被关了就回最后一个置顶页签 */
  function closeAll(): string | undefined {
    const keep = tabs.value.filter((t) => t.pinned)
    if (keep.length === tabs.value.length) return
    tabs.value = keep
    persist()
    if (!keep.some((t) => t.path === active.value)) {
      return keep.at(-1)?.path ?? HOME
    }
  }

  /** 关闭其他：只留置顶的和当前页 */
  function closeOthers(): string | undefined {
    const keep = tabs.value.filter((t) => t.pinned || t.path === active.value)
    if (keep.length === tabs.value.length) return
    tabs.value = keep
    persist()
  }

  return { tabs, active, visit, close, togglePin, closeAll, closeOthers }
})
