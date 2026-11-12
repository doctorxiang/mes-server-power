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
      meta: { title: '工单管理' },
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
      path: '/analysis',
      name: 'analysis',
      component: () => import('@/views/AnalysisView.vue'),
      meta: { title: '良率分析', roles: ['Admin', 'Planner', 'Leader', 'Qc', 'Viewer'] },
    },
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
