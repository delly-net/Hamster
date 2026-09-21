import './assets/main.css'

import { createApp } from 'vue'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { setUnauthorizedHandler } from './api/http'
import { loadAppConfig } from './config/appConfig'
import { useAuthStore } from './stores/auth'

// 挂载前先载入运行时配置，保证页面发起请求时后端基址已就绪
await loadAppConfig()

const app = createApp(App)

app.use(createPinia())

// 恢复登录态需先于 router 安装：否则首次导航的守卫会因尚未恢复而误判为未登录
const auth = useAuthStore()
await auth.restore()

// 令牌失效时统一清理登录态并回到登录页，避免各页面重复处理
setUnauthorizedHandler(() => {
  auth.logout()

  const current = router.currentRoute.value
  if (current.name !== 'login') {
    void router.replace({ name: 'login', query: { redirect: current.fullPath } })
  }
})

app.use(router)

app.mount('#app')
