import './assets/main.css'

import { createApp } from 'vue'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { loadAppConfig } from './config/appConfig'

// 挂载前先载入运行时配置，保证页面发起请求时后端基址已就绪
await loadAppConfig()

const app = createApp(App)

app.use(createPinia())
app.use(router)

app.mount('#app')
