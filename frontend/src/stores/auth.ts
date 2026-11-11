import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { authApi } from '@/api/mes'
import { clearSession, notifySessionChanged, persistSession, readSession, SESSION_EVENT, USER_KEY } from '@/api/session'
import type { AuthResponse, User } from '@/types'

export const useAuthStore = defineStore('auth', () => {
  const initial = readSession()

  const accessToken = ref(initial.accessToken)
  const refreshToken = ref(initial.refreshToken)
  const user = ref<User | null>(initial.user)
  const loading = ref(false)

  const isAuthenticated = computed(() => Boolean(accessToken.value))
  const role = computed(() => user.value?.role ?? '')
  const displayName = computed(() => user.value?.displayName || user.value?.userName || '')
  const canPlan = computed(() => ['Admin', 'Planner'].includes(role.value))
  const canHandleQuality = computed(() => ['Admin', 'Leader', 'Qc'].includes(role.value))
  const isAdmin = computed(() => role.value === 'Admin')

  function syncFromStorage() {
    const s = readSession()
    accessToken.value = s.accessToken
    refreshToken.value = s.refreshToken
    user.value = s.user
  }

  window.addEventListener(SESSION_EVENT, syncFromStorage)

  function setSession(data: AuthResponse) {
    persistSession(data)
    syncFromStorage()
  }

  function clear() {
    clearSession()
    notifySessionChanged()
    syncFromStorage()
  }

  async function login(userNameOrEmail: string, password: string) {
    loading.value = true
    try {
      const { data } = await authApi.login(userNameOrEmail, password)
      setSession(data)
      return data.user
    } finally {
      loading.value = false
    }
  }

  async function logout() {
    const token = refreshToken.value
    try {
      if (token) await authApi.logout(token)
    } catch {
      // 网络异常也要清本地会话
    } finally {
      clear()
    }
  }

  async function fetchMe() {
    if (!accessToken.value) return null
    try {
      const { data } = await authApi.me()
      user.value = data
      localStorage.setItem(USER_KEY, JSON.stringify(data))
      return data
    } catch {
      return null
    }
  }

  async function changePassword(currentPassword: string, newPassword: string) {
    const { data } = await authApi.changePassword(currentPassword, newPassword)
    setSession(data)
  }

  return {
    accessToken,
    refreshToken,
    user,
    loading,
    isAuthenticated,
    role,
    displayName,
    canPlan,
    canHandleQuality,
    isAdmin,
    clear,
    login,
    logout,
    fetchMe,
    changePassword,
  }
})
