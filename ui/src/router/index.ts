import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '../views/HomeView.vue'
import { useAuthStore } from '@/stores/auth'

declare module 'vue-router' {
  interface RouteMeta {
    /** 需要登录态，未登录时由守卫重定向到登录页。 */
    requiresAuth?: boolean
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
      path: '/about',
      name: 'about',
      // route level code-splitting
      // this generates a separate chunk (About.[hash].js) for this route
      // which is lazy-loaded when the route is visited.
      component: () => import('../views/AboutView.vue'),
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
      // 接口调试面板，仅在需要时载入；不依赖登录态，保持公开
      component: () => import('../views/OpenApiView.vue'),
    },
  ],
})

router.beforeEach((to) => {
  const auth = useAuthStore()

  if (to.meta.requiresAuth === true && !auth.isAuthenticated) {
    // 记录来源页，登录成功后跳回
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  if (to.name === 'login' && auth.isAuthenticated) {
    return { name: 'home' }
  }

  return true
})

export default router
