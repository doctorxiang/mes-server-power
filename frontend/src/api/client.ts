import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios'
import type { AuthResponse, ProblemDetails } from '@/types'
import { clearSession, notifySessionChanged, persistSession, readSession } from './session'

export const api = axios.create({
  baseURL: '/api',
  timeout: 15000,
})

api.interceptors.request.use((config) => {
  const token = readSession().accessToken
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

// 单飞刷新：并发 401 只触发一次 /auth/refresh
let refreshing: Promise<string> | null = null

async function refreshAccessToken(): Promise<string> {
  const { refreshToken } = readSession()
  if (!refreshToken) throw new Error('NO_REFRESH_TOKEN')

  const { data } = await axios.post<AuthResponse>('/api/auth/refresh', { refreshToken })
  persistSession(data)
  notifySessionChanged()
  return data.accessToken
}

api.interceptors.response.use(
  (response) => response,
  async (error: AxiosError<ProblemDetails>) => {
    const original = error.config as (InternalAxiosRequestConfig & { _retry?: boolean }) | undefined
    const status = error.response?.status
    const url = original?.url ?? ''
    const isAuthEndpoint = /\/(auth\/login|auth\/refresh)$/.test(url)

    if (status === 401 && original && !original._retry && !isAuthEndpoint) {
      original._retry = true
      try {
        refreshing ??= refreshAccessToken().finally(() => {
          refreshing = null
        })
        const token = await refreshing
        original.headers.Authorization = `Bearer ${token}`
        return api(original)
      } catch {
        clearSession()
        notifySessionChanged()
        if (!window.location.pathname.startsWith('/login')) {
          window.location.replace('/login?expired=1')
        }
        return Promise.reject(error)
      }
    }

    return Promise.reject(error)
  },
)

/** 后端 ProblemDetails -> 一句可展示的中文提示 */
export function parseApiError(error: unknown): string {
  if (axios.isAxiosError<ProblemDetails>(error)) {
    const data = error.response?.data
    if (data?.errors) {
      const first = Object.values(data.errors).flat()[0]
      if (first) return first
    }
    if (data?.detail) return data.detail
    if (error.code === 'ECONNABORTED') return '请求超时，请稍后重试'
    if (!error.response) return '无法连接服务器，请确认后端已启动'
    return `请求失败（${error.response.status}）`
  }
  return error instanceof Error ? error.message : '发生未知错误'
}
