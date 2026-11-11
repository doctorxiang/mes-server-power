import type { AuthResponse, User } from '@/types'

export const TOKEN_KEY = 'mes_token'
export const REFRESH_KEY = 'mes_refresh'
export const USER_KEY = 'mes_user'
export const STATION_KEY = 'mes_station'

/** 会话变化事件：拦截器刷新令牌后通知 Pinia store 同步 */
export const SESSION_EVENT = 'mes:session-changed'

export interface StoredSession {
  accessToken: string
  refreshToken: string
  user: User | null
}

export function readSession(): StoredSession {
  let user: User | null = null
  try {
    const raw = localStorage.getItem(USER_KEY)
    user = raw ? (JSON.parse(raw) as User) : null
  } catch {
    user = null
  }

  return {
    accessToken: localStorage.getItem(TOKEN_KEY) ?? '',
    refreshToken: localStorage.getItem(REFRESH_KEY) ?? '',
    user,
  }
}

export function persistSession(data: AuthResponse): void {
  localStorage.setItem(TOKEN_KEY, data.accessToken)
  localStorage.setItem(REFRESH_KEY, data.refreshToken)
  localStorage.setItem(USER_KEY, JSON.stringify(data.user))
}

export function clearSession(): void {
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(REFRESH_KEY)
  localStorage.removeItem(USER_KEY)
}

export function notifySessionChanged(): void {
  window.dispatchEvent(new Event(SESSION_EVENT))
}

/** 当前工位记忆在本地：扫码枪终端通常固定在一个工位，刷新页面不用重选 */
export function readStation(): string {
  return localStorage.getItem(STATION_KEY) ?? ''
}

export function saveStation(code: string): void {
  localStorage.setItem(STATION_KEY, code)
}
