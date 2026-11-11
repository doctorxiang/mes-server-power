<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import AppShell from '@/components/AppShell.vue'
import { mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import type { MasterBootstrap, Workstation } from '@/types'
import { formatDuration } from '@/utils/format'

const boot = ref<MasterBootstrap | null>(null)
const loading = ref(true)
const error = ref('')
const tab = ref<'products' | 'lines' | 'defects'>('products')

const stationsByLine = computed(() => {
  const groups = new Map<number, Workstation[]>()
  const stations = boot.value?.workstations ?? []
  for (const s of stations) {
    const list = groups.get(s.lineId) ?? []
    list.push(s)
    groups.set(s.lineId, list)
  }
  return groups
})

const defectsByCategory = computed(() => {
  const groups = new Map<string, NonNullable<MasterBootstrap['defectCodes']>>()
  for (const d of boot.value?.defectCodes ?? []) {
    const list = groups.get(d.category) ?? []
    list.push(d)
    groups.set(d.category, list)
  }
  return [...groups.entries()]
})

async function load() {
  loading.value = true
  try {
    const { data } = await mesApi.bootstrap()
    boot.value = data
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <AppShell title="主数据" subtitle="产品工艺路线、产线工位与不良代码字典">
    <div v-if="error" class="alert alert-error" style="margin-bottom: 16px">{{ error }}</div>
    <div v-if="loading" class="empty">加载中…</div>

    <template v-else-if="boot">
      <div class="tabs">
        <button class="tab" :class="{ active: tab === 'products' }" @click="tab = 'products'">产品与工艺路线</button>
        <button class="tab" :class="{ active: tab === 'lines' }" @click="tab = 'lines'">产线与工位</button>
        <button class="tab" :class="{ active: tab === 'defects' }" @click="tab = 'defects'">不良代码</button>
      </div>

      <div v-if="tab === 'products'" class="product-grid">
        <section v-for="p in boot.products" :key="p.id" class="card">
          <div class="card-head">
            <div>
              <h3>{{ p.name }}</h3>
              <div class="small muted mono">{{ p.code }} · 标准节拍 {{ p.standardCycleSeconds }}s</div>
            </div>
            <span class="badge badge-blue">{{ p.route.length }} 道工序</span>
          </div>
          <div class="card-body">
            <div class="route">
              <div v-for="(s, i) in p.route" :key="s.id" class="route-step" :class="{ insp: s.isInspection }">
                <span class="rs-seq">{{ i + 1 }}</span>
                <div class="rs-name">{{ s.name }}</div>
                <div class="rs-meta">
                  <span class="mono">{{ s.code }}</span>
                  <span>{{ formatDuration(s.standardSeconds) }}</span>
                </div>
                <span v-if="s.isKey" class="rs-key">关键</span>
              </div>
            </div>
          </div>
        </section>
      </div>

      <div v-else-if="tab === 'lines'" class="line-grid">
        <section v-for="l in boot.lines" :key="l.id" class="card">
          <div class="card-head">
            <div>
              <h3>{{ l.name }}</h3>
              <div class="small muted mono">{{ l.code }}</div>
            </div>
            <span class="badge">{{ stationsByLine.get(l.id)?.length ?? 0 }} 个工位</span>
          </div>
          <div class="card-body" style="padding: 0">
            <table class="table">
              <thead>
                <tr><th>工位</th><th>名称</th><th>绑定工序</th></tr>
              </thead>
              <tbody>
                <tr v-for="s in stationsByLine.get(l.id) ?? []" :key="s.id">
                  <td class="mono">{{ s.code }}</td>
                  <td>{{ s.name }}</td>
                  <td class="small muted">{{ s.stepName ?? '未绑定' }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>
      </div>

      <div v-else class="defect-grid-layout">
        <section v-for="[category, list] in defectsByCategory" :key="category" class="card">
          <div class="card-head">
            <h3>{{ category }}</h3>
            <span class="badge">{{ list.length }}</span>
          </div>
          <div class="card-body" style="padding: 0">
            <table class="table">
              <thead><tr><th>代码</th><th>不良名称</th></tr></thead>
              <tbody>
                <tr v-for="d in list" :key="d.id">
                  <td class="mono">{{ d.code }}</td>
                  <td>{{ d.name }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </template>
  </AppShell>
</template>

<style scoped>
.product-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(460px, 1fr));
  gap: 16px;
}

.route {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.route-step {
  position: relative;
  min-width: 104px;
  padding: 9px 10px;
  border: 1px solid var(--border);
  border-radius: 9px;
  background: var(--surface-2);
}

.route-step.insp {
  background: #f5f3ff;
  border-color: #ddd6fe;
}

.rs-seq {
  font-family: var(--mono);
  font-size: 10px;
  color: var(--muted);
}

.rs-name {
  font-size: 13px;
  font-weight: 600;
  margin: 1px 0 3px;
}

.rs-meta {
  display: flex;
  flex-direction: column;
  font-size: 10.5px;
  color: var(--muted);
}

.rs-key {
  position: absolute;
  top: 7px;
  right: 7px;
  font-size: 9.5px;
  background: var(--warn-soft);
  color: #92400e;
  padding: 1px 5px;
  border-radius: 4px;
}

.line-grid,
.defect-grid-layout {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(360px, 1fr));
  gap: 16px;
  align-items: start;
}
</style>
