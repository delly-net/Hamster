/**
 * 认证状态：持有登录令牌与当前用户，并封装注册、登录、登出与登录态恢复。
 */

import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'
import { clearToken, getToken, setToken } from '@/auth/token'

/** 当前登录用户（后端不会回传任何凭据字段）。 */
export interface AuthUser {
  id: number
  username: string
  createdAt: string
  /** 是否系统管理员，决定是否展示「用户管理」入口。 */
  isAdmin: boolean
  /** 是否已激活；未激活账号无法登录，故登录态下的用户恒为 `true`。 */
  isActive: boolean
}

/** 登录接口的响应体。 */
interface AuthResponse {
  token: string
  expiresAt: string
  user: AuthUser
}

/** 注册接口的响应体：注册不再签发令牌，需管理员激活后才能登录。 */
interface RegisterResponse {
  message: string
  user: AuthUser
}

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string | null>(getToken())
  const user = ref<AuthUser | null>(null)
  const restoring = ref(false)

  const isAuthenticated = computed(() => token.value !== null)

  /**
   * 是否系统管理员。
   * 仅用于控制入口的显隐，**不是**安全边界：管理端接口在后端逐个回查数据库鉴权，
   * 篡改本地状态只会看到一个请求全部失败的页面。
   */
  const isAdmin = computed(() => user.value?.isAdmin === true)

  /** 记录令牌并同步本地存储。 */
  function applyToken(next: string): void {
    token.value = next
    setToken(next)
  }

  /** 清空登录态。 */
  function logout(): void {
    clearToken()
    token.value = null
    user.value = null
  }

  /**
   * 拉取当前用户信息。
   *
   * @throws 令牌失效（401）时抛出 `ApiError`。
   */
  async function fetchMe(): Promise<AuthUser> {
    const current = await request<AuthUser>('/api/auth/me')
    user.value = current
    return current
  }

  /**
   * 应用启动时按本地令牌恢复登录态。
   * 令牌缺失或已失效时静默清空，不阻断页面加载。
   */
  async function restore(): Promise<void> {
    if (token.value === null) {
      return
    }

    restoring.value = true
    try {
      await fetchMe()
    } catch {
      logout()
    } finally {
      restoring.value = false
    }
  }

  /** 登录。凭据错误时后端返回 401，此处不触发全局登录失效处理。 */
  async function login(username: string, password: string): Promise<AuthUser> {
    const result = await request<AuthResponse>('/api/auth/login', {
      method: 'POST',
      body: { username, password },
      handleUnauthorized: false,
    })

    applyToken(result.token)
    user.value = result.user
    return result.user
  }

  /**
   * 注册。
   *
   * 注册**不**签发令牌：新账号默认未激活，须管理员激活后才能登录，
   * 故这里不改动本地登录态，由调用方提示用户等待激活。
   *
   * @returns 后端返回的提示文案。
   */
  async function register(username: string, password: string): Promise<string> {
    const result = await request<RegisterResponse>('/api/auth/register', {
      method: 'POST',
      body: { username, password },
      handleUnauthorized: false,
    })

    return result.message
  }

  return {
    token,
    user,
    restoring,
    isAuthenticated,
    isAdmin,
    login,
    register,
    logout,
    fetchMe,
    restore,
  }
})
