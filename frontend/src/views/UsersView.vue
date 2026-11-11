<script setup lang="ts">
import { onMounted, ref } from 'vue'
import AppShell from '@/components/AppShell.vue'
import { authApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import type { User } from '@/types'
import { ROLE_LABELS } from '@/types'
import { formatDate, formatTime } from '@/utils/format'

const users = ref<User[]>([])
const loading = ref(true)
const error = ref('')
const notice = ref('')

const showCreate = ref(false)
const saving = ref(false)
const formError = ref('')
const form = ref({ userName: '', displayName: '', employeeNo: '', password: '', role: 'Operator' })

const roles = Object.entries(ROLE_LABELS)

async function load() {
  loading.value = true
  try {
    const { data } = await authApi.users()
    users.value = data
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    loading.value = false
  }
}

async function createUser() {
  formError.value = ''
  if (!form.value.userName.trim() || !form.value.displayName.trim() || form.value.password.length < 8) {
    formError.value = '用户名、姓名必填，密码至少 8 位且包含字母和数字'
    return
  }
  saving.value = true
  try {
    await authApi.createUser({
      userName: form.value.userName.trim(),
      displayName: form.value.displayName.trim(),
      employeeNo: form.value.employeeNo.trim() || undefined,
      password: form.value.password,
      role: form.value.role,
    })
    showCreate.value = false
    form.value = { userName: '', displayName: '', employeeNo: '', password: '', role: 'Operator' }
    notice.value = '账号已创建'
    await load()
  } catch (e) {
    formError.value = parseApiError(e)
  } finally {
    saving.value = false
  }
}

async function updateUser(u: User, patch: { role?: string; isActive?: boolean }) {
  notice.value = ''
  try {
    const { data } = await authApi.updateUser(u.id, patch)
    const idx = users.value.findIndex((x) => x.id === u.id)
    if (idx >= 0) users.value[idx] = data
    notice.value = `已更新 ${data.displayName}`
  } catch (e) {
    error.value = parseApiError(e)
  }
}

onMounted(load)
</script>

<template>
  <AppShell title="账号管理" subtitle="车间账号由管理员统一创建，按角色控制权限">
    <template #actions>
      <button class="btn btn-primary" @click="showCreate = true">＋ 新建账号</button>
    </template>

    <div v-if="error" class="alert alert-error" style="margin-bottom: 14px">{{ error }}</div>
    <div v-if="notice" class="alert alert-ok" style="margin-bottom: 14px">{{ notice }}</div>

    <section class="card">
      <div v-if="loading" class="empty">加载中…</div>
      <div v-else class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th>用户名</th>
              <th>姓名</th>
              <th>工号</th>
              <th>角色</th>
              <th>状态</th>
              <th>创建时间</th>
              <th>最近登录</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="u in users" :key="u.id">
              <td class="mono">{{ u.userName }}</td>
              <td>{{ u.displayName }}</td>
              <td class="mono small">{{ u.employeeNo ?? '—' }}</td>
              <td>
                <select class="select" style="height: 30px; width: 132px; font-size: 12.5px" :value="u.role" @change="updateUser(u, { role: ($event.target as HTMLSelectElement).value })">
                  <option v-for="[value, label] in roles" :key="value" :value="value">{{ label }}</option>
                </select>
              </td>
              <td>
                <span class="badge" :class="u.isActive ? 'badge-ok' : 'badge-danger'">{{ u.isActive ? '启用' : '停用' }}</span>
              </td>
              <td class="small muted">{{ formatDate(u.createdAtUtc) }}</td>
              <td class="small muted">{{ formatTime(u.lastLoginAtUtc) }}</td>
              <td>
                <button class="btn btn-sm" @click="updateUser(u, { isActive: !u.isActive })">
                  {{ u.isActive ? '停用' : '启用' }}
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <div v-if="showCreate" class="modal-mask" @click.self="showCreate = false">
      <div class="modal">
        <div class="modal-head">
          <h3>新建账号</h3>
          <button class="btn btn-sm btn-ghost" @click="showCreate = false">关闭</button>
        </div>
        <div class="modal-body">
          <div v-if="formError" class="alert alert-error">{{ formError }}</div>
          <div class="grid-2">
            <div class="field">
              <label>用户名</label>
              <input v-model="form.userName" class="input" placeholder="如 operator3" />
            </div>
            <div class="field">
              <label>姓名</label>
              <input v-model="form.displayName" class="input" placeholder="如 赵伟" />
            </div>
          </div>
          <div class="grid-2">
            <div class="field">
              <label>工号</label>
              <input v-model="form.employeeNo" class="input" placeholder="如 O0003" />
            </div>
            <div class="field">
              <label>角色</label>
              <select v-model="form.role" class="select">
                <option v-for="[value, label] in roles" :key="value" :value="value">{{ label }}</option>
              </select>
            </div>
          </div>
          <div class="field">
            <label>初始密码</label>
            <input v-model="form.password" class="input" type="password" placeholder="至少 8 位，含字母和数字" />
          </div>
        </div>
        <div class="modal-foot">
          <button class="btn" @click="showCreate = false">取消</button>
          <button class="btn btn-primary" :disabled="saving" @click="createUser">{{ saving ? '创建中…' : '创建' }}</button>
        </div>
      </div>
    </div>
  </AppShell>
</template>

<style scoped>
.table-wrap {
  overflow-x: auto;
}
</style>
