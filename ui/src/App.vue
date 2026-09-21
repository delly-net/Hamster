<script setup lang="ts">
import { computed, watchEffect } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import HelloWorld from './components/HelloWorld.vue'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

/**
 * 空白布局（由路由 `meta.layout = 'blank'` 指定，如登录页）：只呈现该页面自身的内容，
 * 隐藏导航、欢迎语与账号区，仅保留一行版权页脚。品牌标识由页面自身承载
 * （如登录页把 logo 放在表单卡片内部），此处不再渲染。
 */
const isBlankLayout = computed(() => route.meta.layout === 'blank')
const currentYear = new Date().getFullYear()

// 根组件是 Fragment，无法直接给 #app 绑定布局类，故落到 body 上供 main.css 覆盖宽屏的两列网格。
// 本组件即根组件、不会卸载，无需清理该 class。
watchEffect(() => {
  document.body.classList.toggle('layout-blank', isBlankLayout.value)
})

/** 退出登录并回到登录页。 */
async function handleLogout(): Promise<void> {
  auth.logout()
  await router.replace({ name: 'login' })
}
</script>

<template>
  <header v-if="!isBlankLayout">
    <img alt="Hamster logo" class="logo" src="@/assets/logo.png" width="125" height="125" />

    <div class="wrapper">
      <HelloWorld msg="仓鼠理财管家" />

      <nav>
        <RouterLink to="/">首页</RouterLink>
        <RouterLink to="/about">关于</RouterLink>
        <RouterLink to="/openapi">接口调试</RouterLink>
        <!-- 仅管理员可见；非管理员即使手敲地址也会被守卫挡回首页 -->
        <RouterLink v-if="auth.isAdmin" to="/admin/users">用户管理</RouterLink>
        <RouterLink v-if="!auth.isAuthenticated" to="/login">登录 / 注册</RouterLink>
      </nav>

      <p v-if="auth.isAuthenticated" class="account">
        <span class="account-name">{{ auth.user?.username ?? '已登录' }}</span>
        <button type="button" class="logout" @click="handleLogout">退出登录</button>
      </p>
    </div>
  </header>

  <RouterView />

  <footer v-if="isBlankLayout" class="site-footer">© {{ currentYear }} 仓鼠理财管家</footer>
</template>

<style scoped>
header {
  line-height: 1.5;
  max-height: 100vh;
}

.logo {
  display: block;
  margin: 0 auto 2rem;
  /* 位图 logo 已带透明圆角，加一层柔和投影使其从奶油渐变底上浮起 */
  filter: drop-shadow(0 10px 18px rgba(90, 58, 34, 0.22));
}

nav {
  width: 100%;
  font-size: 12px;
  text-align: center;
  margin-top: 2rem;
}

nav a.router-link-exact-active {
  color: var(--color-accent-strong);
  font-weight: 600;
}

nav a.router-link-exact-active:hover {
  background-color: transparent;
}

nav a {
  display: inline-block;
  padding: 0 1rem;
  border-left: 1px solid var(--color-border);
}

nav a:first-of-type {
  border: 0;
}

.account {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: 1rem;
  font-size: 12px;
}

.site-footer {
  margin-top: 2rem;
  text-align: center;
  font-size: 12px;
  opacity: 0.6;
}

.account-name {
  opacity: 0.75;
}

.logout {
  padding: 0.3rem 0.7rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: none;
  color: var(--color-accent-strong);
  font-size: 12px;
  font-family: inherit;
  cursor: pointer;
  transition:
    background-color 0.3s,
    color 0.3s;
}

@media (hover: hover) {
  .logout:hover {
    background-color: var(--color-accent-soft);
  }
}

@media (min-width: 1024px) {
  header {
    display: flex;
    place-items: center;
    padding-right: calc(var(--section-gap) / 2);
  }

  .logo {
    margin: 0 2rem 0 0;
  }

  header .wrapper {
    display: flex;
    place-items: flex-start;
    flex-wrap: wrap;
  }

  nav {
    text-align: left;
    margin-left: -1rem;
    font-size: 1rem;

    padding: 1rem 0;
    margin-top: 1rem;
  }

  .account {
    font-size: 13px;
  }
}
</style>
