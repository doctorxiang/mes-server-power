const pad = (n: number) => String(n).padStart(2, '0')

/** 后端时间为 UTC ISO 字符串，统一按浏览器本地时区展示 */
export function formatTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export function formatClock(d: Date): string {
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

export function formatDate(iso: string | null | undefined): string {
  if (!iso) return '—'
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
