export interface AuditRow {
  id: number
  userId: number | null
  userName: string | null
  role: string | null
  method: string
  path: string
  action: string | null
  statusCode: number
  success: boolean
  message: string | null
  clientIp: string | null
  requestId: string | null
  durationMs: number
  createdAtUtc: string
}

export interface AuditDetail extends AuditRow {
  payload: string | null
}

export interface PagedResult<T> {
  total: number
  page: number
  size: number
  items: T[]
}

// ---------- APS 排程 ----------
export interface ApsScheduleRow {
  orderId: number
  orderNo: string
  productName: string
  lineName: string
  qty: number
  status: string
  bottleneckStep: string
  bottleneckTaktSeconds: number
  unitsPerHour: number
  startLocal: string
  endLocal: string
  dueDateLocal: string
  onTime: boolean
  loadHours: number
}

export interface ApsResult {
  generatedAtLocal: string
  totalHours: number
  plannedOrders: number
  rows: ApsScheduleRow[]
  bottleneckNotes: string[]
}

// ---------- 良率分析 ----------
export interface YieldStepRow {
  stepName: string
  passQty: number
  failQty: number
  yieldPercent: number
}

export interface YieldDefectRow {
  code: string
  name: string
  count: number
  cumulativePercent: number
}

export interface YieldRow {
  key: string
  displayName: string
  orderStatus: string | null
  inputQty: number
  completedQty: number
  holdQty: number
  failQty: number
  yieldPercent: number
  firstPassYield: number
  steps: YieldStepRow[]
  topDefects: YieldDefectRow[]
}

export interface YieldResult {
  by: string
  rows: YieldRow[]
}

// ---------- 文件（图纸/SOP/检验报告） ----------
export interface FileDocRow {
  id: number
  category: string
  productId: number | null
  productName: string | null
  workOrderId: number | null
  workOrderNo: string | null
  sn: string | null
  fileName: string
  sizeBytes: number
  contentType: string
  remark: string | null
  uploadedByName: string
  createdAtUtc: string
}

export interface User {
  id: number
  userName: string
  displayName: string
  employeeNo: string | null
  role: string
  isActive: boolean
  createdAtUtc: string
  lastLoginAtUtc: string | null
}

export interface AuthResponse {
  accessToken: string
  expiresIn: number
  refreshToken: string
  user: User
}

export interface ProblemDetails {
  status?: number
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

export interface Step {
  id: number
  code: string
  name: string
  kind: string
  requireDefect: boolean
  isKey: boolean
  seq: number
  standardSeconds: number
  isInspection: boolean
}

export interface Product {
  id: number
  code: string
  name: string
  category: string
  standardCycleSeconds: number
  isActive: boolean
  route: Step[]
}

export interface Line {
  id: number
  code: string
  name: string
  isActive: boolean
}

export interface Workstation {
  id: number
  code: string
  name: string
  lineId: number
  stepId: number | null
  stepName: string | null
}

export interface DefectCode {
  id: number
  code: string
  name: string
  category: string
}

export interface MasterBootstrap {
  lines: Line[]
  workstations: Workstation[]
  defectCodes: DefectCode[]
  products: Product[]
  steps: Step[]
}

export interface WorkOrderListItem {
  id: number
  orderNo: string
  productCode: string
  productName: string
  lineId: number
  lineName: string
  planQty: number
  inputQty: number
  completedQty: number
  holdQty: number
  status: string
  priority: number
  plannedStartUtc: string
  dueDateUtc: string
  createdAtUtc: string
  /** 累计发料（计划 + 补料），产线只能在这里面投产 */
  issuedQty: number
  /** 已报废台数（千分之几，报废一台补一台） */
  scrapQty: number
  /** 报废后还没补的缺口，>0 时必须补料才能继续上线 */
  replenishQty: number
  /** 返修台数：进过维修站的不同 SN 数 */
  repairUnits: number
  /** 返修次数：维修记录条数（同一台修两次算两次） */
  repairTimes: number
}

export interface StepProgress {
  stepId: number
  stepCode: string
  stepName: string
  seq: number
  isInspection: boolean
  passQty: number
  failQty: number
  wipQty: number
}

export interface UnitRow {
  id: number
  sn: string
  currentSeq: number
  currentStepName: string | null
  status: string
  createdAtUtc: string
  completedAtUtc: string | null
  lastScanAtUtc: string
}

/** 报废明细：哪台、哪个工序判的、什么不良、什么时候报废的 */
export interface ScrapUnitRow {
  sn: string
  stepName: string
  defectName: string
  scrappedAtUtc: string | null
  reason: string
}

export interface WorkOrderDetail {
  summary: WorkOrderListItem
  steps: StepProgress[]
  recentUnits: UnitRow[]
  fpy: number
  scrapUnits: ScrapUnitRow[]
}

export interface ScanResponse {
  accepted: boolean
  duplicated: boolean
  message: string
  sn: string
  orderNo: string
  stepCode: string
  stepName: string
  seq: number
  totalSteps: number
  unitStatus: string
  nextStepCode: string | null
  nextStepName: string | null
  todayStepPassQty: number
  todayStepFailQty: number
}

export interface RecentLog {
  id: number
  sn: string
  stepName: string
  result: string
  defectName: string | null
  operatorName: string
  stationCode: string
  createdAtUtc: string
}

export interface TraceStep {
  seq: number
  stepCode: string
  stepName: string
  stepId: number
  result: string | null
  defectName: string | null
  operatorName: string | null
  stationCode: string | null
  scanAtUtc: string | null
  durationSeconds: number
  remark: string | null
}

export interface TraceResponse {
  sn: string
  orderNo: string
  productCode: string
  productName: string
  lineName: string
  status: string
  currentStepName: string
  createdAtUtc: string
  completedAtUtc: string | null
  passCount: number
  failCount: number
  steps: TraceStep[]
  repairs: RepairRecordRow[]
}

// ---------------- 维修站 ----------------

export interface RepairPendingRow {
  unitId: number
  sn: string
  orderNo: string
  productName: string
  lineName: string
  currentStepName: string
  currentSeq: number
  defectCode: string | null
  defectName: string | null
  repairCount: number
  repairing: boolean
  failedAtUtc: string
}

export interface RepairRecordRow {
  id: number
  sn: string
  repairNo: number
  sourceStepName: string
  defectName: string | null
  action: string
  status: 'InProgress' | 'Repaired' | 'Scrapped'
  operatorName: string
  stationCode: string | null
  checkoutByName: string | null
  startedAtUtc: string
  completedAtUtc: string | null
  remark: string | null
}

export const REPAIR_ACTION_LABELS: Record<string, string> = {
  Replace: '换料',
  Resolder: '补焊',
  Adjust: '调整',
  Other: '其它',
}

export const REPAIR_STATUS_LABELS: Record<string, string> = {
  InProgress: '维修中',
  Repaired: '修复出站',
  Scrapped: '报废出站',
}

/** 维修站筛选条件：日期 + 产线 + 工序 + 不良代码 + 工单号，队列与历史共用 */
export interface RepairQuery {
  limit?: number
  orderNo?: string
  lineId?: number
  stepId?: number
  defectCode?: string
  status?: string
  from?: string
  to?: string
}

/** 维修历史（维修站「维修记录」Tab）：按日期/产线/工序/不良代码翻历史 */
export interface RepairHistoryRow {
  id: number
  sn: string
  orderNo: string
  productName: string
  lineName: string
  sourceStepName: string
  repairNo: number
  defectCode: string | null
  defectName: string | null
  action: string
  status: 'InProgress' | 'Repaired' | 'Scrapped'
  operatorName: string
  checkoutByName: string | null
  startedAtUtc: string
  completedAtUtc: string | null
  remark: string | null
}

// ---------------- QC 抽检（已包装成品） ----------------

export interface QcInspectionRow {
  id: number
  sn: string
  orderNo: string
  productName: string
  lineName: string
  category: string
  result: 'Pass' | 'Fail'
  defectCode: string | null
  defectName: string | null
  inspectorName: string
  inspectedAtUtc: string
  remark: string | null
}

export interface QcSummary {
  inspected: number
  pass: number
  fail: number
  passRate: number
  orders: number
  lines: number
}

export interface QcInspectionResult {
  summary: QcSummary
  rows: QcInspectionRow[]
}

export interface QcCandidateRow {
  unitId: number
  sn: string
  orderNo: string
  productName: string
  lineName: string
  completedAtUtc: string | null
  inspectedTimes: number
}

export const QC_CATEGORIES = ['包装', '外观', '功能', '安规'] as const

export interface StepWip {
  stepCode: string
  stepName: string
  wipQty: number
}

export interface HourPoint {
  hour: string
  pass: number
  fail: number
}

export interface DefectTop {
  code: string
  name: string
  category: string
  qty: number
}

export interface LineSummary {
  lineId: number
  lineName: string
  planQty: number
  inputQty: number
  completedQty: number
  holdQty: number
  todayPass: number
  todayFail: number
  fpy: number
  wipByStep: StepWip[]
}

export interface Dashboard {
  date: string
  shift: string
  generatedAtUtc: string
  planQty: number
  inputQty: number
  completedQty: number
  holdQty: number
  todayPass: number
  todayFail: number
  fpy: number
  openExceptions: number
  lines: LineSummary[]
  hourly: HourPoint[]
  wipByStep: StepWip[]
  topDefects: DefectTop[]
  recent: RecentLog[]
}

export interface ExceptionRow {
  id: number
  type: string
  description: string
  status: string
  lineName: string
  orderNo: string | null
  stationCode: string | null
  reportedByName: string
  reportedAtUtc: string
  handledByName: string | null
  handleNote: string | null
  handledAtUtc: string | null
}

export const ROLE_LABELS: Record<string, string> = {
  Admin: '系统管理员',
  Planner: 'PMC 计划员',
  Leader: '班组长',
  Operator: '操作工',
  Qc: '质检员',
  Viewer: '只读',
}

export const STATUS_LABELS: Record<string, string> = {
  Draft: '草稿',
  Released: '已下发',
  InProduction: '生产中',
  Completed: '已完工',
  Closed: '已关单',
  InProcess: '在制',
  Hold: '不良隔离',
  Scrapped: '报废',
}

export const EXCEPTION_TYPES: Record<string, string> = {
  Material: '缺料',
  Equipment: '设备故障',
  Quality: '品质异常',
  Other: '其他',
}
