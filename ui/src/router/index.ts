import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '../views/HomeView.vue'
import { useAuthStore } from '@/stores/auth'

declare module 'vue-router' {
  interface RouteMeta {
    /** 需要登录态，未登录时由守卫重定向到登录页。 */
    requiresAuth?: boolean
    /**
     * 需要系统管理员身份，非管理员由守卫重定向到首页。
     * 与 {@link requiresAuth} 并列使用，仅在登录态已知时生效。
     */
    requiresAdmin?: boolean
    /** 布局模式：`blank` 为空白布局（仅品牌 logo + 极简页脚），缺省为完整头部布局。 */
    layout?: 'blank'
  }
}

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'home',
      component: HomeView,
      // 记账数据与登录用户绑定，未登录时由守卫重定向到登录页
      meta: { requiresAuth: true },
    },
    {
      path: '/accounts',
      name: 'accounts',
      // 账户管理（新建/编辑/停用，归属当前账套），面向所有登录用户，故不设 requiresAdmin
      component: () => import('../views/AccountView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/categories',
      name: 'categories',
      // 分类管理（新建/改名/停用，归属当前账套），面向所有登录用户，故不设 requiresAdmin：
      // 分类是账套内成员共用的字典，而记账时手工输入即会自动建分类，把维护收进管理端毫无道理
      component: () => import('../views/CategoryView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/income',
      name: 'income',
      // 记一笔收入（选定账户 + 金额 + 摘要即落表），面向所有登录用户，故不设 requiresAdmin。
      // **与 /expense 各自独立组件**：共用一个组件时两条路由会复用实例、切换不重新挂载，
      // 用户已填的金额与摘要会残留到另一种记账上
      component: () => import('../views/IncomeView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/expense',
      name: 'expense',
      // 记一笔支出，与 /income 同构、仅方向相反，故理由同上
      component: () => import('../views/ExpenseView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/transfer',
      name: 'transfer',
      // 记一笔转账（转出账户 + 转入账户 + 金额 + 摘要即落表），面向所有登录用户，故不设 requiresAdmin。
      // 与 /income、/expense **各自独立组件**，理由同上面两条：共用组件会让路由切换复用实例、
      // 已填的金额与摘要残留到另一种记账上
      component: () => import('../views/TransferView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/entries',
      name: 'entries',
      // 账目明细查询（按时间区间与账户筛选当前账套内的交易明细），面向所有登录用户，故不设 requiresAdmin
      component: () => import('../views/EntryQueryView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/settings',
      name: 'settings',
      // 用户设置（修改密码 + 软件关于信息），面向所有登录用户，故不设 requiresAdmin
      component: () => import('../views/SettingsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/login',
      name: 'login',
      // 登录/注册表单，与首页同属首屏路径，按需载入
      component: () => import('../views/LoginView.vue'),
      // 未登录时不应暴露应用内的导航与其他模块入口，故走空白布局
      meta: { layout: 'blank' },
    },
    {
      path: '/openapi',
      name: 'openapi',
      // 接口调试面板，仅在需要时载入。
      // 菜单入口仅对系统管理员展示（见 `config/menu.ts` 的 adminOnly），
      // 但本路由**未加守卫、仍保持公开**：直达地址对任何访客可用。
      component: () => import('../views/OpenApiView.vue'),
    },
    {
      path: '/admin/users',
      name: 'admin-users',
      // 用户管理（激活/停用、重置链接、删除），仅管理员可见
      component: () => import('../views/UserAdminView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/admin/account-sets',
      name: 'admin-account-sets',
      // 账套管理（新增/改名/删除、关联用户），仅管理员可见
      component: () => import('../views/AccountSetAdminView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/admin/currencies',
      name: 'admin-currencies',
      // 币种管理（新增/改名/设为默认/停用），仅管理员可见。
      // 币种是全局字典，与账套无关，故本路由不随当前账套变化
      component: () => import('../views/CurrencyAdminView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/reset-password',
      name: 'reset-password',
      // 密码重置落地页：令牌经查询参数传入，**必须免登录**，
      // 否则被重置的用户（多半处于未登录态）打不开链接
      component: () => import('../views/ResetPasswordView.vue'),
      meta: { layout: 'blank' },
    },
  ],
})

router.beforeEach((to) => {
  const auth = useAuthStore()

  if (to.meta.requiresAuth === true && !auth.isAuthenticated) {
    // 记录来源页，登录成功后跳回
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  // 非管理员直接回首页：这里只是体验层的兜底，真正的防线是后端逐个管理端点的数据库回查
  if (to.meta.requiresAdmin === true && !auth.isAdmin) {
    return { name: 'home' }
  }

  if (to.name === 'login' && auth.isAuthenticated) {
    return { name: 'home' }
  }

  return true
})

export default router
