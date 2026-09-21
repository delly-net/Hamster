/**
 * 左侧功能菜单配置。
 *
 * 菜单项以**路由名**（而非硬编码路径）指向路由表，路径调整不影响菜单；
 * 新增功能页时只需在此追加一项，`App.vue` 无需改动。
 */
export interface MenuItem {
  /** 菜单文案 */
  label: string
  /** 目标路由的 name，须与 `router/index.ts` 中声明的 name 一致 */
  routeName: string
  /** 仅系统管理员可见；非管理员不渲染该项（真正的防线是路由守卫与后端逐个端点的库回查） */
  adminOnly?: boolean
}

export const menuItems: MenuItem[] = [
  { label: '首页', routeName: 'home' },
  { label: '关于', routeName: 'about' },
  { label: '接口调试', routeName: 'openapi' },
  { label: '用户管理', routeName: 'admin-users', adminOnly: true },
]
