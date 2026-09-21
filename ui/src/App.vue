<script setup lang="ts">
import { RouterLink, RouterView, useRouter } from 'vue-router'
import HelloWorld from './components/HelloWorld.vue'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const router = useRouter()

/** 退出登录并回到登录页。 */
async function handleLogout(): Promise<void> {
  auth.logout()
  await router.replace({ name: 'login' })
}
</script>

<template>
  <header>
    <img alt="Hamster logo" class="logo" src="@/assets/logo.svg" width="125" height="125" />

    <div class="wrapper">
      <HelloWorld msg="仓鼠理财管家" />

      <nav>
        <RouterLink to="/">首页</RouterLink>
        <RouterLink to="/about">关于</RouterLink>
        <RouterLink to="/openapi">接口调试</RouterLink>
        <RouterLink v-if="!auth.isAuthenticated" to="/login">登录 / 注册</RouterLink>
      </nav>

      <p v-if="auth.isAuthenticated" class="account">
        <span class="account-name">{{ auth.user?.username ?? '已登录' }}</span>
        <button type="button" class="logout" @click="handleLogout">退出登录</button>
      </p>
    </div>
  </header>

  <RouterView />
</template>

<style scoped>
header {
  line-height: 1.5;
  max-height: 100vh;
}

.logo {
  display: block;
  margin: 0 auto 2rem;
}

nav {
  width: 100%;
  font-size: 12px;
  text-align: center;
  margin-top: 2rem;
}

nav a.router-link-exact-active {
  color: var(--color-text);
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

.account-name {
  opacity: 0.75;
}

.logout {
  padding: 0.3rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: 6px;
  background: none;
  color: inherit;
  font-size: 12px;
  font-family: inherit;
  cursor: pointer;
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
