import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
      meta: { public: true, title: '登录' },
    },
    { path: '/', redirect: '/work-orders' },
    {
      path: '/work-orders',
      name: 'work-orders',
      component: () => import('@/views/WorkOrdersView.vue'),
      meta: { title: '工单列表' },
    },
    // 工单报告挂在工单管理下（子菜单），路径写在 :id 前面，避免被当成工单 Id
    {
      path: '/work-orders/report',
      name: 'work-order-report',
      component: () => import('@/views/AnalysisView.vue'),
      meta: { title: '工单报告', roles: ['Admin', 'Planner', 'Leader', 'Qc', 'Viewer'] },
    },
    {
      path: '/work-orders/:id',
      name: 'work-order-detail',
      component: () => import('@/views/WorkOrderDetailView.vue'),
      meta: { title: '工单详情' },
    },
    {
      path: '/scan',
      name: 'scan',
      component: () => import('@/views/ScanView.vue'),
      meta: { title: '扫码报工' },
    },
    {
      path: '/dashboard',
      name: 'dashboard',
      component: () => import('@/views/DashboardView.vue'),
      meta: { title: '车间看板' },
    },
    {
      path: '/trace',
      name: 'trace',
      component: () => import('@/views/TraceView.vue'),
      meta: { title: 'SN 追溯' },
    },
    {
      path: '/repair',
      name: 'repair',
      component: () => import('@/views/RepairView.vue'),
      meta: { title: '维修站', roles: ['Admin', 'Leader', 'Qc'] },
    },
    {
      path: '/qc',
      name: 'qc',
      component: () => import('@/views/QcView.vue'),
      meta: { title: 'QC 抽检', roles: ['Admin', 'Leader', 'Qc', 'Viewer'] },
    },
    // 老的收藏夹 / 文档链接：良率分析已并入工单管理下的「工单报告」
    { path: '/analysis', redirect: '/work-orders/report' },
    {
      path: '/aps',
      name: 'aps',
      component: () => import('@/views/ApsView.vue'),
      meta: { title: 'APS 排程', roles: ['Admin', 'Planner', 'Leader'] },
    },
    {
      path: '/files',
      name: 'files',
      component: () => import('@/views/FilesView.vue'),
      meta: { title: '工艺文件' },
    },
    {
      path: '/master',
      name: 'master',
      component: () => import('@/views/MasterView.vue'),
      meta: { title: '主数据' },
    },
    {
      path: '/users',
      name: 'users',
      component: () => import('@/views/UsersView.vue'),
      meta: { title: '账号管理', roles: ['Admin'] },
    },
    {
      path: '/audit',
      name: 'audit',
      component: () => import('@/views/AuditView.vue'),
      meta: { title: '操作审计', roles: ['Admin'] },
    },
    { path: '/:pathMatch(.*)*', redirect: '/work-orders' },
  ],
  scrollBehavior: () => ({ top: 0 }),
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()

  if (to.meta.public) {
    return auth.isAuthenticated && to.name === 'login' ? { path: '/work-orders' } : true
  }

  if (!auth.isAuthenticated) {
    return { path: '/login', query: { redirect: to.fullPath } }
  }

  const roles = to.meta.roles as string[] | undefined
  if (roles && !roles.includes(auth.role)) {
    return { path: '/work-orders' }
  }

  if (!auth.user) await auth.fetchMe()
  return true
})

router.afterEach((to) => {
  const title = to.meta.title as string | undefined
  document.title = title ? `${title} · 电源 MES` : '电源 MES'
})

export default router
