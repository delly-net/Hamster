<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch, watchEffect } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { menuItems } from '@/config/menu'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

/** 产品名：header 品牌区与空白布局页脚共用。 */
const productName = '仓鼠理财管家'
const currentYear = new Date().getFullYear()

/**
 * 空白布局（由路由 `meta.layout = 'blank'` 指定，如登录页）：只呈现该页面自身的内容，
 * 不渲染 header、功能菜单与内容框架，仅保留一行版权页脚。品牌标识由页面自身承载
 * （如登录页把 logo 放在表单卡片内部），此处不再渲染。
 */
const isBlankLayout = computed(() => route.meta.layout === 'blank')

/** 窄屏抽屉的开合状态；宽屏下侧栏常驻，该状态不参与渲染。 */
const sidebarOpen = ref(false)

/** 按权限过滤后的菜单项：非管理员看不到「用户管理」。 */
const visibleMenuItems = computed(() =>
  menuItems.filter((item) => !item.adminOnly || auth.isAdmin),
)

// 根组件是 Fragment，无法直接给 #app 绑定布局类，故落到 body 上供 main.css 切换布局模式。
// 本组件即根组件、不会卸载，无需清理该 class。
// 抽屉展开无需另加背景滚动锁：非空白布局下 #app 已是整屏高度 + overflow: hidden。
watchEffect(() => {
  document.body.classList.toggle('layout-blank', isBlankLayout.value)
})

// 窄屏下点菜单会跳转，若不自动收起，抽屉会盖住刚打开的内容区
watch(
  () => route.fullPath,
  () => {
    sidebarOpen.value = false
  },
)

/** `Esc` 收起抽屉。 */
function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && sidebarOpen.value) {
    sidebarOpen.value = false
  }
}

// 根组件常驻不卸载，监听挂一次即可；仍保留卸载清理，避免布局重构后变成泄漏
onMounted(() => window.addEventListener('keydown', handleKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', handleKeydown))

/** 退出登录并回到登录页。 */
async function handleLogout(): Promise<void> {
  auth.logout()
  await router.replace({ name: 'login' })
}
</script>

<template>
  <template v-if="isBlankLayout">
    <RouterView />

    <footer class="site-footer">© {{ currentYear }} {{ productName }}</footer>
  </template>

  <template v-else>
    <header class="app-header">
      <div class="brand">
        <button
          type="button"
          class="menu-toggle"
          :aria-expanded="sidebarOpen"
          aria-controls="app-sidebar"
          aria-label="展开功能菜单"
          @click="sidebarOpen = !sidebarOpen"
        >
          <span class="menu-toggle-bar" />
          <span class="menu-toggle-bar" />
          <span class="menu-toggle-bar" />
        </button>

        <img alt="Hamster logo" class="brand-logo" src="@/assets/logo.png" width="34" height="34" />
        <span class="brand-name">{{ productName }}</span>
      </div>

      <div class="account">
        <template v-if="auth.isAuthenticated">
          <span class="account-name">{{ auth.user?.username ?? '已登录' }}</span>
          <button type="button" class="logout" @click="handleLogout">退出登录</button>
        </template>
        <RouterLink v-else class="login-link" to="/login">登录 / 注册</RouterLink>
      </div>
    </header>

    <div class="app-body">
      <aside id="app-sidebar" class="app-sidebar" :class="{ open: sidebarOpen }">
        <nav class="menu">
          <RouterLink
            v-for="item in visibleMenuItems"
            :key="item.routeName"
            class="menu-item"
            :to="{ name: item.routeName }"
          >
            {{ item.label }}
          </RouterLink>
        </nav>
      </aside>

      <!-- 窄屏抽屉遮罩：仅在展开时渲染，点击收起 -->
      <div v-if="sidebarOpen" class="drawer-mask" @click="sidebarOpen = false" />

      <main class="app-main">
        <RouterView />
      </main>
    </div>
  </template>
</template>

<style scoped>
.app-header {
  flex: none;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  height: 56px;
  padding: 0 1.25rem;
  border-bottom: 1px solid var(--color-border);
  background-color: var(--color-background-soft);
}

.brand {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  min-width: 0;
}

.brand-logo {
  display: block;
  /* 位图 logo 已带透明圆角，加一层柔和投影使其从浅色底上浮起（沿用登录页的暖棕投影） */
  filter: drop-shadow(0 4px 8px rgba(90, 58, 34, 0.22));
}

.brand-name {
  font-size: 15px;
  font-weight: 600;
  color: var(--color-heading);
  white-space: nowrap;
}

.account {
  flex: none;
  display: flex;
  align-items: center;
  gap: 0.6rem;
  font-size: 13px;
}

.account-name {
  opacity: 0.75;
}

.login-link {
  font-size: 13px;
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

.menu-toggle {
  display: none;
  flex-direction: column;
  justify-content: center;
  gap: 4px;
  width: 34px;
  height: 34px;
  padding: 0 7px;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: none;
  cursor: pointer;
}

.menu-toggle-bar {
  display: block;
  height: 2px;
  border-radius: 1px;
  background-color: var(--color-accent-strong);
}

.app-body {
  position: relative;
  flex: 1;
  display: flex;
  /*
   * min-height: 0 是「仅内容区滚动」的关键：flex 子项默认 min-height: auto 会被内容撑高，
   * 缺了它 .app-main 的 overflow-y 不生效，页面会退化成整页滚动。
   */
  min-height: 0;
}

.app-sidebar {
  flex: none;
  width: 200px;
  padding: 0.75rem;
  border-right: 1px solid var(--color-border);
  background-color: var(--color-background-soft);
  overflow-y: auto;
}

.menu {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.menu-item {
  padding: 0.5rem 0.75rem;
  border-radius: var(--radius-control);
  font-size: 14px;
  color: var(--color-text);
}

@media (hover: hover) {
  .menu-item:hover {
    background-color: var(--color-accent-soft);
  }
}

/* 活跃态放在 hover 之后：同特异性下靠后覆盖，悬停到已激活项也保持高亮 */
.menu-item.router-link-active {
  background-color: var(--color-accent-soft);
  color: var(--color-accent-strong);
  font-weight: 600;
}

.app-main {
  flex: 1;
  /* 允许内部宽表格/长串收缩，不撑破 flex 行 */
  min-width: 0;
  padding: 1.5rem 2rem;
  overflow-y: auto;
}

.drawer-mask {
  position: fixed;
  inset: 56px 0 0 0;
  z-index: 19;
  /* 遮罩非品牌色，用中性半透明黑压暗即可 */
  background-color: rgba(0, 0, 0, 0.32);
}

.site-footer {
  margin-top: 2rem;
  text-align: center;
  font-size: 12px;
  opacity: 0.6;
}

/* 窄屏：功能菜单收起为抽屉，由 header 内的汉堡按钮开合 */
@media (max-width: 1023px) {
  .menu-toggle {
    display: flex;
  }

  .app-sidebar {
    position: fixed;
    top: 56px;
    bottom: 0;
    left: 0;
    z-index: 20;
    transform: translateX(-100%);
    transition: transform 0.25s ease;
    box-shadow: var(--shadow-card);
  }

  .app-sidebar.open {
    transform: none;
  }

  .app-main {
    padding: 1.25rem 1rem;
  }
}
</style>
