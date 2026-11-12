const pad = (n: number) => String(n).padStart(2, '0')

/** 后端时间为 UTC ISO 字符串，统一按浏览器本地时区展示 */
export function formatTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** 顶栏时钟的日期部分：2026-11-12 周四（车间看时间先看日期，再对秒） */
export function formatClockDate(d: Date): string {
  const weekday = '日一二三四五六'[d.getDay()]
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} 周${weekday}`
}

/** 顶栏时钟的时间部分：时:分:秒（每秒刷新，等宽字体避免跳动） */
export function formatClock(d: Date): string {
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

/** 审计场景精确到秒：排查问题时分钟级不够用 */
export function formatTimeSec(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

export function formatDate(iso: string | null | undefined): string {  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/** 秒 -> 1h 20m / 45s */
export function formatDuration(seconds: number): string {
  if (!seconds || seconds < 0) return '—'
  if (seconds < 60) return `${seconds}s`
  const m = Math.floor(seconds / 60)
  if (m < 60) return `${m}m${seconds % 60 ? ` ${seconds % 60}s` : ''}`
  const h = Math.floor(m / 60)
  return `${h}h ${m % 60}m`
}

export function percent(part: number, total: number): number {
  if (!total) return 0
  return Math.round((part / total) * 100)
}

export function elapsedFrom(iso: string): string {
  const start = new Date(iso).getTime()
  if (Number.isNaN(start)) return '—'
  const diff = Math.max(0, Math.floor((Date.now() - start) / 1000))
  return formatDuration(diff)
}
