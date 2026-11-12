<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import AppShell from '@/components/AppShell.vue'
import { fileApi, mesApi } from '@/api/mes'
import { parseApiError } from '@/api/client'
import type { FileDocRow, MasterBootstrap } from '@/types'
import { formatTimeSec } from '@/utils/format'

const CATEGORIES = [
  { value: 'Drawing', label: '图纸' },
  { value: 'SOP', label: 'SOP 作业指导书' },
  { value: 'InspectionReport', label: '检验报告' },
  { value: 'Photo', label: '现场照片' },
  { value: 'Other', label: '其它' },
]

const boot = ref<MasterBootstrap | null>(null)
const rows = ref<FileDocRow[]>([])
const total = ref(0)
const page = ref(1)
const size = 30
const storage = ref('')
const loading = ref(false)
const error = ref('')
const notice = ref('')

const filter = ref({ category: '', productId: '' as string | number })

// 上传表单
const uploadCategory = ref('Drawing')
const uploadProductId = ref<string | number>('')
const uploadSn = ref('')
const uploadRemark = ref('')
const fileInput = ref<HTMLInputElement | null>(null)
const uploading = ref(false)

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / size)))

function categoryLabel(v: string) {
  return CATEGORIES.find((c) => c.value === v)?.label ?? v
}

function sizeText(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const { data } = await fileApi.list({
      category: filter.value.category || undefined,
      productId: filter.value.productId ? Number(filter.value.productId) : undefined,
      page: page.value,
      size,
    })
    rows.value = data.items
    total.value = data.total
    storage.value = data.storage
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    loading.value = false
  }
}

async function upload() {
  const file = fileInput.value?.files?.[0]
  if (!file) {
    error.value = '请先选择文件'
    return
  }
  if (file.size > 20 * 1024 * 1024) {
    error.value = '单个文件不能超过 20MB'
    return
  }

  uploading.value = true
  error.value = ''
  notice.value = ''
  try {
    const form = new FormData()
    form.append('file', file)
    form.append('category', uploadCategory.value)
    if (uploadProductId.value) form.append('productId', String(uploadProductId.value))
    if (uploadSn.value.trim()) form.append('sn', uploadSn.value.trim().toUpperCase())
    if (uploadRemark.value.trim()) form.append('remark', uploadRemark.value.trim())

    await fileApi.upload(form)
    notice.value = `已上传：${file.name}`
    uploadSn.value = ''
    uploadRemark.value = ''
    if (fileInput.value) fileInput.value.value = ''
    page.value = 1
    await load()
  } catch (e) {
    error.value = parseApiError(e)
  } finally {
    uploading.value = false
  }
}

async function download(row: FileDocRow) {
  try {
    const { data } = await fileApi.download(row.id)
    const url = URL.createObjectURL(data)
    const a = document.createElement('a')
    a.href = url
    a.download = row.fileName
    a.click()
    URL.revokeObjectURL(url)
  } catch (e) {
    error.value = parseApiError(e)
  }
}

async function remove(row: FileDocRow) {
  if (!window.confirm(`确认删除「${row.fileName}」？对象存储中的文件也会一并删除`)) return
  try {
    await fileApi.remove(row.id)
    notice.value = '已删除'
    await load()
  } catch (e) {
    error.value = parseApiError(e)
  }
}

function search() {
  page.value = 1
  load()
}

function go(delta: number) {
  const next = page.value + delta
  if (next < 1 || next > pageCount.value) return
  page.value = next
  load()
}

onMounted(async () => {
  await load()
  try {
    boot.value = (await mesApi.bootstrap()).data
  } catch {
    // 机种下拉拿不到不影响文件列表
  }
})
</script>

<template>
  <AppShell title="工艺文件" subtitle="图纸 / SOP / 检验报告统一归档：内容进对象存储，元数据可检索">
    <div v-if="error" class="alert alert-error" style="margin-bottom: 14px">{{ error }}</div>
    <div v-if="notice" class="alert alert-ok" style="margin-bottom: 14px">{{ notice }}</div>

    <div class="layout">
      <section class="card">
        <div class="card-head">
          <h3>上传文件</h3>
          <span class="small muted">单文件 ≤ 20MB · 存储：{{ storage || '—' }}</span>
        </div>
        <div class="card-body">
          <label class="field">
            <span class="small muted">分类</span>
            <select v-model="uploadCategory" class="select">
              <option v-for="c in CATEGORIES" :key="c.value" :value="c.value">{{ c.label }}</option>
            </select>
          </label>
          <label class="field">
            <span class="small muted">关联机种（图纸 / SOP 建议必选）</span>
            <select v-model="uploadProductId" class="select">
              <option value="">不关联</option>
              <option v-for="p in boot?.products ?? []" :key="p.id" :value="p.id">{{ p.code }} · {{ p.name }}</option>
            </select>
          </label>
          <label class="field">
            <span class="small muted">关联 SN（单台检验报告可填）</span>
            <input v-model="uploadSn" class="input mono" placeholder="可选" />
          </label>
          <label class="field">
            <span class="small muted">备注</span>
            <input v-model="uploadRemark" class="input" maxlength="256" placeholder="可选，如：V1.2 修订版" />
          </label>
          <label class="field">
            <span class="small muted">文件</span>
            <input ref="fileInput" type="file" class="input" />
          </label>
          <button class="btn btn-primary" style="margin-top: 6px" :disabled="uploading" @click="upload">
            {{ uploading ? '上传中…' : '上传' }}
          </button>
        </div>
      </section>

      <section class="card">
        <div class="card-head">
          <h3>文件列表</h3>
          <span class="small muted">共 {{ total }} 个</span>
        </div>
        <div class="card-body">
          <div class="filters">
            <select v-model="filter.category" class="select" style="width: 180px" @change="search">
              <option value="">全部分类</option>
              <option v-for="c in CATEGORIES" :key="c.value" :value="c.value">{{ c.label }}</option>
            </select>
            <select v-model="filter.productId" class="select" style="width: 220px" @change="search">
              <option value="">全部机种</option>
              <option v-for="p in boot?.products ?? []" :key="p.id" :value="p.id">{{ p.code }}</option>
            </select>
            <button class="btn" :disabled="loading" @click="load">{{ loading ? '加载中…' : '刷新' }}</button>
          </div>

          <div v-if="!rows.length && !loading" class="empty">还没有文件，先从左侧上传一份</div>
          <table v-else class="table">
            <thead>
              <tr>
                <th>文件</th>
                <th>分类</th>
                <th>关联</th>
                <th>大小</th>
                <th>上传</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="r in rows" :key="r.id">
                <td>
                  <div>{{ r.fileName }}</div>
                  <div v-if="r.remark" class="small muted">{{ r.remark }}</div>
                </td>
                <td><span class="badge">{{ categoryLabel(r.category) }}</span></td>
                <td class="small">
                  <div v-if="r.productName">{{ r.productName }}</div>
                  <div v-if="r.workOrderNo" class="mono muted">{{ r.workOrderNo }}</div>
                  <div v-if="r.sn" class="mono muted">{{ r.sn }}</div>
                  <span v-if="!r.productName && !r.workOrderNo && !r.sn" class="muted">—</span>
                </td>
                <td class="small mono">{{ sizeText(r.sizeBytes) }}</td>
                <td class="small muted">{{ r.uploadedByName }} · {{ formatTimeSec(r.createdAtUtc) }}</td>
                <td style="white-space: nowrap">
                  <button class="btn btn-sm" @click="download(r)">下载</button>
                  <button class="btn btn-sm btn-ghost" @click="remove(r)">删除</button>
                </td>
              </tr>
            </tbody>
          </table>

          <div class="pager">
            <span class="small muted">第 {{ page }} / {{ pageCount }} 页</span>
            <div style="display: flex; gap: 8px">
              <button class="btn btn-sm" :disabled="page <= 1 || loading" @click="go(-1)">上一页</button>
              <button class="btn btn-sm" :disabled="page >= pageCount || loading" @click="go(1)">下一页</button>
            </div>
          </div>
        </div>
      </section>
    </div>
  </AppShell>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: 320px 1fr;
  gap: 14px;
  align-items: start;
}
@media (max-width: 1000px) {
  .layout { grid-template-columns: 1fr; }
}
.field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  margin-bottom: 11px;
}
.filters {
  display: flex;
  gap: 10px;
  margin-bottom: 12px;
  flex-wrap: wrap;
}
.pager {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding-top: 12px;
}
</style>
