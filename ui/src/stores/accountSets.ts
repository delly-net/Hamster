/**
 * 账套状态。
 *
 * 分两侧职责：
 * - **用户侧**：我可访问的账套、当前账套、登录后是否必须先选择（多账套弹窗）。
 * - **管理侧**：面向系统管理员的账套增删改与关联用户维护。
 *
 * 用户侧持有的当前账套只是偏好值，**不是安全边界**：后端在每次请求上校验该账套与
 * 当前用户的关联关系，篡改本地状态只会看到一批 403。
 */

import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'
import { clearAccountSetId, getAccountSetId, setAccountSetId } from '@/auth/accountSet'

/** 账套（后端不回传任何内部明细）。 */
export interface AccountSet {
  id: number
  name: string
  remark: string | null
  /** 关联用户数；仅管理员列表返回，其余场景为 `null`。 */
  memberCount: number | null
  /** 创建时间（UTC，ISO 8601）。 */
  createdAt: string
}

/** 用户侧接口基址。 */
const ACCOUNT_SETS_PATH = '/api/account-sets'

/** 管理侧接口基址。 */
const ADMIN_ACCOUNT_SETS_PATH = '/api/admin/account-sets'

export const useAccountSetsStore = defineStore('accountSets', () => {
  // ---------- 用户侧 ----------

  /** 当前用户可访问的账套。 */
  const accountSets = ref<AccountSet[]>([])
  /** 当前账套 Id；`null` 表示尚未选定或无可用账套。 */
  const currentId = ref<number | null>(getAccountSetId())
  const loading = ref(false)
  /** 是否已完成过一次拉取（用于区分「尚未加载」与「确实一个账套都没有」）。 */
  const loaded = ref(false)
  /** 可访问账套多于一个且尚未选定：登录后须先弹出选择弹窗。 */
  const selectionRequired = ref(false)
  /** 切换面板是否打开（由 header 的【切换】按钮触发）。 */
  const pickerOpen = ref(false)

  /** 当前账套实体；未选定或已不在可访问列表中时为 `null`。 */
  const current = computed(
    () => accountSets.value.find((item) => item.id === currentId.value) ?? null,
  )

  /** 一个可访问账套都没有时的提示文案；正常状态下为空串。 */
  const emptyNotice = computed(() =>
    loaded.value && accountSets.value.length === 0 ? '暂无可用账套' : '',
  )

  /** 写入当前账套：同步内存状态与本地存储；`null` 表示「无当前账套」。 */
  function applyCurrent(id: number | null): void {
    currentId.value = id
    if (id === null) {
      clearAccountSetId()
    } else {
      setAccountSetId(id)
    }
  }

  /** 依最新账套列表收敛当前账套与「是否必须先选择」。 */
  function syncCurrent(): void {
    if (accountSets.value.length === 0) {
      applyCurrent(null)
      selectionRequired.value = false
      return
    }

    // 本地记录的账套仍可访问：保留，刷新页面后停留在上次选择
    if (currentId.value !== null && accountSets.value.some((item) => item.id === currentId.value)) {
      selectionRequired.value = false
      return
    }

    // 仅一个账套：自动选中，不打扰用户
    const only = accountSets.value[0]
    if (accountSets.value.length === 1 && only !== undefined) {
      applyCurrent(only.id)
      selectionRequired.value = false
      return
    }

    // 多个账套且无有效当前账套：交给界面弹出选择弹窗
    applyCurrent(null)
    selectionRequired.value = true
  }

  /**
   * 校验后端是否认可当前账套。
   *
   * 账套关联可能被管理员随时调整（如刚取消了对本用户的关联），而本地记录不会自动失效，
   * 故拉取列表后额外过一次 `/current`：后端会校验账套存在且当前用户有权访问，
   * 被拒则清空当前账套（多账套时改为要求重新选择），避免带着失效账套继续操作。
   *
   * @returns 后端认可当前账套返回 `true`。
   */
  async function verifyCurrent(): Promise<boolean> {
    if (currentId.value === null) {
      return false
    }

    try {
      await request<AccountSet | null>(`${ACCOUNT_SETS_PATH}/current`)
      return true
    } catch {
      applyCurrent(null)
      selectionRequired.value = accountSets.value.length > 1
      return false
    }
  }

  /**
   * 拉取当前用户可访问的账套，并据此收敛当前账套。
   *
   * @throws 令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadMine(): Promise<void> {
    loading.value = true
    try {
      accountSets.value = await request<AccountSet[]>(`${ACCOUNT_SETS_PATH}/mine`)
      loaded.value = true
      syncCurrent()

      if (currentId.value !== null) {
        await verifyCurrent()
      }
    } finally {
      loading.value = false
    }
  }

  /** 选定账套（首次选择与切换共用）。 */
  function select(id: number): void {
    applyCurrent(id)
    selectionRequired.value = false
    pickerOpen.value = false
  }

  /** 打开切换面板，并重新拉取列表（管理员调整关联后无需刷新整页即可看到）。 */
  async function openPicker(): Promise<void> {
    pickerOpen.value = true
    try {
      await loadMine()
    } catch {
      // 拉取失败时保留已有列表，面板照常可用
    }
  }

  /** 关闭切换面板；「必须先选择」时不允许关闭，避免绕过选择直接使用系统。 */
  function closePicker(): void {
    if (selectionRequired.value) {
      return
    }

    pickerOpen.value = false
  }

  /** 清空全部用户侧状态（退出登录时调用）。 */
  function clear(): void {
    accountSets.value = []
    loaded.value = false
    selectionRequired.value = false
    pickerOpen.value = false
    applyCurrent(null)
  }

  // ---------- 管理侧 ----------

  /** 管理端的账套列表（含关联用户数）。 */
  const adminList = ref<AccountSet[]>([])
  const adminLoading = ref(false)

  /** 拉取全部账套。 */
  async function listAccountSets(): Promise<AccountSet[]> {
    adminLoading.value = true
    try {
      const result = await request<AccountSet[]>(ADMIN_ACCOUNT_SETS_PATH)
      adminList.value = result
      return result
    } finally {
      adminLoading.value = false
    }
  }

  /** 新建账套。名称重复时后端返回 409。 */
  async function createAccountSet(name: string, remark: string): Promise<void> {
    await request<AccountSet>(ADMIN_ACCOUNT_SETS_PATH, {
      method: 'POST',
      body: { name, remark },
    })
  }

  /** 修改账套名称与备注。 */
  async function updateAccountSet(id: number, name: string, remark: string): Promise<void> {
    await request<void>(`${ADMIN_ACCOUNT_SETS_PATH}/${id}`, {
      method: 'PUT',
      body: { name, remark },
    })
  }

  /** 删除账套；其关联用户同时被清除。 */
  async function deleteAccountSet(id: number): Promise<void> {
    await request<void>(`${ADMIN_ACCOUNT_SETS_PATH}/${id}`, { method: 'DELETE' })
  }

  /** 读取某账套已关联的用户 Id。 */
  async function loadMembers(id: number): Promise<number[]> {
    return await request<number[]>(`${ADMIN_ACCOUNT_SETS_PATH}/${id}/members`)
  }

  /** 覆盖式保存某账套的关联用户。 */
  async function saveMembers(id: number, userIds: number[]): Promise<void> {
    await request<void>(`${ADMIN_ACCOUNT_SETS_PATH}/${id}/members`, {
      method: 'PUT',
      body: { userIds },
    })
  }

  return {
    accountSets,
    currentId,
    current,
    loading,
    loaded,
    emptyNotice,
    selectionRequired,
    pickerOpen,
    loadMine,
    verifyCurrent,
    select,
    openPicker,
    closePicker,
    clear,
    adminList,
    adminLoading,
    listAccountSets,
    createAccountSet,
    updateAccountSet,
    deleteAccountSet,
    loadMembers,
    saveMembers,
  }
})
