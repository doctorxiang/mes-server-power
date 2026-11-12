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

export interface WorkOrderDetail {
  summary: WorkOrderListItem
  steps: StepProgress[]
  recentUnits: UnitRow[]
  fpy: number
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
