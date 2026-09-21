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
}

/** 注册/登录接口的响应体。 */
interface AuthResponse {
  token: string
  expiresAt: string
  user: AuthUser
}

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string | null>(getToken())
  const user = ref<AuthUser | null>(null)
  const restoring = ref(false)

  const isAuthenticated = computed(() => token.value !== null)

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

  /** 注册；成功后即视为登录。 */
  async function register(username: string, password: string): Promise<AuthUser> {
    const result = await request<AuthResponse>('/api/auth/register', {
      method: 'POST',
      body: { username, password },
      handleUnauthorized: false,
    })

    applyToken(result.token)
    user.value = result.user
    return result.user
  }

  return { token, user, restoring, isAuthenticated, login, register, logout, fetchMe, restore }
})
