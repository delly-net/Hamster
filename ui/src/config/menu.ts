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
  // 收支记账是最高频的日常动作，紧随首页；两者各自独立入口（页内逻辑共用同一表单组件）
  { label: '收入', routeName: 'income' },
  { label: '支出', routeName: 'expense' },
  // 账户属于日常记账入口，面向所有登录用户，故不设 adminOnly
  { label: '账户管理', routeName: 'accounts' },
  // 查账入口，与账户管理同属面向所有登录用户的日常功能
  { label: '账目明细', routeName: 'entries' },
  { label: '接口调试', routeName: 'openapi', adminOnly: true },
  { label: '用户管理', routeName: 'admin-users', adminOnly: true },
  { label: '账套管理', routeName: 'admin-account-sets', adminOnly: true },
  // 面向所有登录用户的个性化入口，固定收尾；软件关于信息已并入该页
  { label: '用户设置', routeName: 'settings' },
]
