/**
 * 登录令牌的本地读写。
 *
 * 以 `localStorage` 持久化，刷新页面后仍保持登录态；隐私模式等场景下
 * `localStorage` 可能不可用，此时静默降级为内存存储，不阻断页面。
 */

/** 令牌在 localStorage 中的键名。 */
const TOKEN_STORAGE_KEY = 'hamster.auth.token'

/** localStorage 不可用时的兜底存储。 */
let memoryToken: string | null = null

/** 读取令牌；不存在时返回 `null`。 */
export function getToken(): string | null {
  try {
    return window.localStorage.getItem(TOKEN_STORAGE_KEY) ?? memoryToken
  } catch {
    return memoryToken
  }
}

/** 写入令牌。 */
export function setToken(token: string): void {
  memoryToken = token
  try {
    window.localStorage.setItem(TOKEN_STORAGE_KEY, token)
  } catch {
    // 降级为内存存储即可，无需打扰用户
  }
}

/** 清除令牌（退出登录或令牌失效时调用）。 */
export function clearToken(): void {
  memoryToken = null
  try {
    window.localStorage.removeItem(TOKEN_STORAGE_KEY)
  } catch {
    // 同上
  }
}
