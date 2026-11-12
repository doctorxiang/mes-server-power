import { api } from './client'
import type {
  ApsResult,
  AuditDetail,
  AuditRow,
  AuthResponse,
  Dashboard,
  ExceptionRow,
  FileDocRow,
  MasterBootstrap,
  PagedResult,
  RecentLog,
  RepairPendingRow,
  RepairRecordRow,
  ScanResponse,
  TraceResponse,
  User,
  WorkOrderDetail,
  WorkOrderListItem,
  YieldResult,
} from '@/types'

export const authApi = {
  login: (userNameOrEmail: string, password: string) =>
    api.post<AuthResponse>('/auth/login', { userNameOrEmail, password }),
  me: () => api.get<User>('/auth/me'),
  logout: (refreshToken: string) => api.post('/auth/logout', { refreshToken }),
  changePassword: (currentPassword: string, newPassword: string) =>
    api.post<AuthResponse>('/auth/change-password', { currentPassword, newPassword }),
  users: () => api.get<User[]>('/auth/users'),
  createUser: (payload: {
    userName: string
    displayName: string
    employeeNo?: string
    password: string
    role: string
  }) => api.post<User>('/auth/users', payload),
  updateUser: (id: number, payload: { displayName?: string; employeeNo?: string; role?: string; isActive?: boolean }) =>
    api.put<User>(`/auth/users/${id}`, payload),
}

export const mesApi = {  bootstrap: () => api.get<MasterBootstrap>('/master/bootstrap'),

  workOrders: (params?: { status?: string; lineId?: number; keyword?: string }) =>
    api.get<WorkOrderListItem[]>('/work-orders', { params }),
  workOrder: (id: number) => api.get<WorkOrderDetail>(`/work-orders/${id}`),
  workOrderByNo: (orderNo: string) => api.get<WorkOrderListItem>(`/work-orders/by-no/${encodeURIComponent(orderNo)}`),
  /** 补料：报废一台补一台，补完才能继续上线 */
  replenishWorkOrder: (id: number, qty: number, reason?: string) =>
    api.post<WorkOrderListItem>(`/work-orders/${id}/replenish`, { qty, reason }),
  createWorkOrder: (payload: {
    productId: number
    lineId: number
    planQty: number
    priority: number
    plannedStartUtc: string
    dueDateUtc: string
    remark?: string
  }) => api.post<WorkOrderDetail>('/work-orders', payload),
  changeWorkOrderStatus: (id: number, action: 'release' | 'close' | 'reopen') =>
    api.post<WorkOrderListItem>(`/work-orders/${id}/status`, null, { params: { action } }),

  scan: (payload: {
    sn: string
    orderNo: string
    stationCode: string
    stepCode: string
    result: 'Pass' | 'Fail'
    defectCode?: string
    remark?: string
    clientKey?: string
  }) => api.post<ScanResponse>('/production/scan', payload),
  recent: (stepCode?: string, limit = 12) =>
    api.get<RecentLog[]>('/production/recent', { params: { stepCode, limit } }),
  handleHold: (sn: string, action: 'rework' | 'scrap', remark?: string) =>
    api.post('/production/hold-handle', { action, remark }, { params: { sn } }),

  repairPending: (limit = 50, orderNo?: string, product?: string) =>
    api.get<RepairPendingRow[]>('/production/repair/pending', { params: { limit, orderNo, product } }),
  repairCheckin: (payload: {
    sn: string
    action: string
    defectCode?: string
    remark?: string
    stationCode?: string
    force?: boolean
  }) => api.post<RepairRecordRow>('/production/repair/checkin', payload),
  repairCheckout: (payload: { sn: string; outcome: 'repaired' | 'scrap'; remark?: string; stationCode?: string }) =>
    api.post<{ sn: string; status: string; message: string }>('/production/repair/checkout', payload),

  dashboard: (lineId?: number) => api.get<Dashboard>('/dashboard', { params: { lineId } }),
  trace: (sn: string) => api.get<TraceResponse>(`/trace/${encodeURIComponent(sn)}`),

  exceptions: (status?: string, lineId?: number) =>
    api.get<ExceptionRow[]>('/exceptions', { params: { status, lineId } }),
  createException: (payload: { type: string; description: string; lineId: number; workOrderId?: number; stationCode?: string }) =>
    api.post<ExceptionRow>('/exceptions', payload),
  closeException: (id: number, handleNote: string) =>
    api.post<ExceptionRow>(`/exceptions/${id}/close`, { handleNote }),
}

/** 审计日志（仅管理员）：客户审厂、质量追责、误操作回放 */
export const auditApi = {
  query: (params: {
    from?: string
    to?: string
    userName?: string
    action?: string
    success?: boolean
    page?: number
    size?: number
  }) => api.get<PagedResult<AuditRow>>('/audit', { params }),
  detail: (id: number) => api.get<AuditDetail>(`/audit/${id}`),
}

/** APS 排程：有限产能正排 */
export const apsApi = {
  run: (includeDraft = false) => api.post<ApsResult>('/aps/run', null, { params: { includeDraft } }),
}

/** 良率 / 不良率分析 */
export const analysisApi = {
  yield: (by: 'order' | 'product', from?: string, to?: string) =>
    api.get<YieldResult>('/analysis/yield', { params: { by, from, to } }),
}

/** 图纸 / SOP / 检验报告等非结构化文件 */
export const fileApi = {
  list: (params: { category?: string; productId?: number; workOrderId?: number; sn?: string; page?: number; size?: number }) =>
    api.get<{ total: number; page: number; size: number; storage: string; items: FileDocRow[] }>('/files', { params }),
  upload: (form: FormData) =>
    api.post<{ id: number; fileName: string; category: string; storage: string }>('/files', form, {
      headers: { 'Content-Type': 'multipart/form-data' },
      timeout: 60000,
    }),
  download: (id: number) => api.get<Blob>(`/files/${id}/content`, { responseType: 'blob' }),
  remove: (id: number) => api.delete<{ message: string }>(`/files/${id}`),
}
