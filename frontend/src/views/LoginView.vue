<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { parseApiError } from '@/api/client'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const userNameOrEmail = ref('')
const password = ref('')
const error = ref('')
const expired = ref(route.query.expired === '1')
const submitting = ref(false)

const demoAccounts = [
  { label: '管理员', user: 'admin', pwd: 'Admin@12345' },
  { label: '班组长', user: 'leader', pwd: 'Leader@123' },
  { label: '操作工', user: 'operator1', pwd: 'Operator@123' },
  { label: '质检员', user: 'qc1', pwd: 'Qc@12345678' },
]

onMounted(() => {
  const remembered = localStorage.getItem('mes_last_user')
  if (remembered) userNameOrEmail.value = remembered
})

function fill(account: (typeof demoAccounts)[number]) {
  userNameOrEmail.value = account.user
  password.value = account.pwd
  error.value = ''
}

async function submit() {
  error.value = ''
  if (!userNameOrEmail.value.trim() || !password.value) {
    error.value = '请输入账号和密码'
    return
  }

  submitting.value = true
  try {
    await auth.login(userNameOrEmail.value.trim(), password.value)
    localStorage.setItem('mes_last_user', userNameOrEmail.value.trim())
    const redirect = (route.query.redirect as string) || '/work-orders'
    router.replace(redirect)
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <section class="brand-panel">
      <div class="brand-badge">MES · SERVER POWER</div>
      <h1>服务器电源<br />制造执行系统</h1>
      <p class="brand-desc">
        从插件、过锡炉、目视、测试到包装，一台一码，全程可追溯。
        扫码枪即插即用，200 台终端同时在线。
      </p>

      <ul class="flow-list">
        <li><b>01</b> 插件</li>
        <li><b>02</b> 过锡炉</li>
        <li><b>03</b> 目视检查</li>
        <li><b>04</b> ICT / 老化 / 耐压 / 功能</li>
        <li><b>05</b> 包装入库</li>
      </ul>

      <div class="brand-foot">
        <div><strong>9</strong><span>道工序管控</span></div>
        <div><strong>100%</strong><span>SN 追溯覆盖</span></div>
        <div><strong>&lt;300ms</strong><span>扫码响应</span></div>
      </div>
    </section>

    <section class="form-panel">
      <div class="login-card">
        <h2>登录</h2>
        <p class="muted small" style="margin-bottom: 22px">使用工号或用户名登录产线终端</p>

        <div v-if="expired" class="alert alert-error" style="margin-bottom: 14px">登录已过期，请重新登录</div>
        <div v-if="error" class="alert alert-error" style="margin-bottom: 14px">{{ error }}</div>

        <form @submit.prevent="submit" style="display: flex; flex-direction: column; gap: 16px">
          <div class="field">
            <label for="user">工号 / 用户名</label>
            <input id="user" v-model="userNameOrEmail" class="input" autocomplete="username" placeholder="例如 operator1" />
          </div>

          <div class="field">
            <label for="pwd">密码</label>
            <input id="pwd" v-model="password" class="input" type="password" autocomplete="current-password" placeholder="••••••••" />
          </div>

          <button class="btn btn-primary btn-block" type="submit" :disabled="submitting" style="height: 42px">
            {{ submitting ? '登录中…' : '登录' }}
          </button>
        </form>

        <div class="demo">
          <div class="demo-title">演示账号（点击填入）</div>
          <div class="demo-grid">
            <button v-for="a in demoAccounts" :key="a.user" class="demo-item" type="button" @click="fill(a)">
              <strong>{{ a.label }}</strong>
              <span class="mono">{{ a.user }}</span>
            </button>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: grid;
  grid-template-columns: 1.05fr 1fr;
  background: var(--surface);
}

.brand-panel {
  background:
    radial-gradient(900px 500px at 15% 10%, rgba(59, 130, 246, 0.28), transparent 60%),
    radial-gradient(700px 500px at 90% 90%, rgba(8, 145, 178, 0.25), transparent 55%),
    #0b1220;
  color: #e2e8f0;
  padding: 64px 56px;
  display: flex;
  flex-direction: column;
  position: relative;
  overflow: hidden;
}

.brand-panel::after {
  content: '';
  position: absolute;
  right: -120px;
  top: 90px;
  width: 340px;
  height: 340px;
  border: 1px solid rgba(148, 163, 184, 0.16);
  border-radius: 50%;
  box-shadow: 0 0 0 40px rgba(148, 163, 184, 0.05), 0 0 0 90px rgba(148, 163, 184, 0.04);
}

.brand-badge {
  display: inline-flex;
  align-self: flex-start;
  padding: 5px 10px;
  border-radius: 999px;
  border: 1px solid rgba(96, 165, 250, 0.4);
  background: rgba(37, 99, 235, 0.18);
  color: #93c5fd;
  font-size: 11px;
  letter-spacing: 1.4px;
}

.brand-panel h1 {
  margin-top: 26px;
  font-size: 40px;
  line-height: 1.22;
  font-weight: 750;
  letter-spacing: -0.5px;
  color: #f8fafc;
}

.brand-desc {
  margin-top: 16px;
  max-width: 420px;
  color: #94a3b8;
  font-size: 14px;
  line-height: 1.8;
}

.flow-list {
  list-style: none;
  padding: 0;
  margin: 34px 0 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
  font-size: 13.5px;
  color: #cbd5e1;
}

.flow-list li {
  display: flex;
  align-items: center;
  gap: 12px;
}

.flow-list b {
  font-family: var(--mono);
  font-size: 11px;
  color: #60a5fa;
  width: 26px;
  height: 26px;
  display: grid;
  place-items: center;
  border-radius: 7px;
  background: rgba(37, 99, 235, 0.16);
  border: 1px solid rgba(96, 165, 250, 0.25);
}

.brand-foot {
  margin-top: auto;
  display: flex;
  gap: 34px;
  padding-top: 40px;
}

.brand-foot div {
  display: flex;
  flex-direction: column;
}

.brand-foot strong {
  font-size: 22px;
  color: #f8fafc;
}

.brand-foot span {
  font-size: 11.5px;
  color: #64748b;
}

.form-panel {
  display: grid;
  place-items: center;
  padding: 40px 24px;
  background: #f8fafc;
}

.login-card {
  width: 100%;
  max-width: 380px;
}

.login-card h2 {
  font-size: 26px;
  letter-spacing: -0.4px;
}

.demo {
  margin-top: 26px;
  padding-top: 18px;
  border-top: 1px dashed var(--border);
}

.demo-title {
  font-size: 11.5px;
  color: var(--muted);
  margin-bottom: 10px;
  letter-spacing: 0.4px;
}

.demo-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

.demo-item {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 1px;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--surface);
  cursor: pointer;
  text-align: left;
  transition: all 0.15s;
}

.demo-item:hover {
  border-color: var(--primary);
  background: var(--primary-soft);
}

.demo-item strong {
  font-size: 12.5px;
  color: var(--text);
}

.demo-item span {
  font-size: 11px;
  color: var(--muted);
}

@media (max-width: 980px) {
  .login-page {
    grid-template-columns: 1fr;
  }

  .brand-panel {
    display: none;
  }
}
</style>
