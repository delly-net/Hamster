/**
 * 当前账套的本地读写。
 *
 * 以 `localStorage` 持久化，刷新页面后仍停留在上次选择的账套；隐私模式等场景下
 * `localStorage` 可能不可用，此时静默降级为内存存储，不阻断页面。
 *
 * 这里存的是**前端偏好值**，不具备任何安全含义：真正的防线是后端在每次请求上
 * 校验该账套与当前用户的关联关系（请求头见 `api/http.ts`）。
 */

/** 当前账套 Id 在 localStorage 中的键名。 */
const ACCOUNT_SET_STORAGE_KEY = 'hamster.accountSet.id'

/** localStorage 不可用时的兜底存储。 */
let memoryAccountSetId: number | null = null

/** 读取当前账套 Id；未选择或存取的值非法时返回 `null`。 */
export function getAccountSetId(): number | null {
  try {
    const raw = window.localStorage.getItem(ACCOUNT_SET_STORAGE_KEY)
    return raw === null ? memoryAccountSetId : parseAccountSetId(raw)
  } catch {
    return memoryAccountSetId
  }
}

/** 写入当前账套 Id。 */
export function setAccountSetId(id: number): void {
  memoryAccountSetId = id
  try {
    window.localStorage.setItem(ACCOUNT_SET_STORAGE_KEY, String(id))
  } catch {
    // 降级为内存存储即可，无需打扰用户
  }
}

/** 清除当前账套（退出登录，或当前账套已不可访问时调用）。 */
export function clearAccountSetId(): void {
  memoryAccountSetId = null
  try {
    window.localStorage.removeItem(ACCOUNT_SET_STORAGE_KEY)
  } catch {
    // 同上
  }
}

/**
 * 把存储中的原始字符串解析为账套 Id。
 * 用 `Number` 而非 `parseInt`：后者会把 `"12abc"` 读成 `12`，让脏值蒙混过关。
 */
function parseAccountSetId(raw: string): number | null {
  const parsed = Number(raw)
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null
}
