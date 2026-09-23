/**
 * 币种状态。
 *
 * 分两侧职责：
 * - **用户侧**：启用的币种字典与系统默认币种，供记账表单与账户新建表单选币种。
 * - **管理侧**：面向系统管理员的币种增删改（软删除）、设默认。
 *
 * **币种是全局字典，不挂在账套下**：本 store 的所有请求都不随当前账套变化，
 * 请求头里的 `X-Account-Set-Id` 也不会被后端读取。
 *
 * 币种代码与名称一律由后端下发，前端**不维护第二份对照表**：币种可由管理员新增与改名，
 * 硬编码一份「CNY → 人民币」只会在管理员改名后与之漂移。
 */

import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/** 币种（后端不回传内部明细）。 */
export interface Currency {
  id: number
  /** ISO 4217 三字母代码，恒为大写（如 `CNY`）。 */
  code: string
  /** 币种中文名（如「人民币」）。 */
  name: string
  /** 币种符号（如 `¥`）；无符号时为 `null`。 */
  symbol: string | null
  /** 是否为**实际生效**的系统默认币种；全表至多一个为 `true`。 */
  isDefault: boolean
  /** 是否启用；停用即软删除。 */
  isActive: boolean
  /** 呈现顺序，越小越靠前。 */
  sortOrder: number
}

/** 新建币种的入参。 */
export interface CreateCurrencyPayload {
  code: string
  name: string
  symbol: string | null
  sortOrder: number
}

/**
 * 修改币种的入参。
 *
 * **没有 `code`**：代码是币种的身份，账户按代码绑定币种，中途改代码等于让所有已绑定的账户
 * 指向另一个币种。后端 `PUT` 同样不读取请求体里的 code。
 */
export interface UpdateCurrencyPayload {
  name: string
  symbol: string | null
  sortOrder: number
}

/** 用户侧接口基址。 */
const CURRENCIES_PATH = '/api/currencies'

/** 管理侧接口基址。 */
const ADMIN_CURRENCIES_PATH = '/api/admin/currencies'

export const useCurrenciesStore = defineStore('currencies', () => {
  // ---------- 用户侧 ----------

  /** 全部**启用**的币种。 */
  const currencies = ref<Currency[]>([])
  const loading = ref(false)
  /** 是否已完成过一次拉取（用于区分「尚未加载」与「确实一个币种都没有」）。 */
  const loaded = ref(false)

  /**
   * 实际生效的默认币种代码；一个币种都没有时为 `null`。
   *
   * 直接取后端标了 `isDefault` 的那一项，而**不**在前端另算一次回退：默认币种被停用时
   * 后端会把回退项也标成 `isDefault`，前端再算一遍只会得到第二套口径。
   */
  const defaultCode = computed(() => currencies.value.find((item) => item.isDefault)?.code ?? null)

  /**
   * 拉取启用的币种字典。
   *
   * @throws 令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadActive(): Promise<Currency[]> {
    loading.value = true
    try {
      const result = await request<Currency[]>(CURRENCIES_PATH)
      currencies.value = result
      loaded.value = true
      return result
    } finally {
      loading.value = false
    }
  }

  /** 按代码取币种；未启用或不存在时为 `null`。 */
  function findByCode(code: string): Currency | null {
    return currencies.value.find((item) => item.code === code) ?? null
  }

  /** 清空用户侧列表（退出登录时调用，避免残留上一账号的字典）。 */
  function clear(): void {
    currencies.value = []
    loaded.value = false
  }

  // ---------- 管理侧 ----------

  /** 管理端的币种列表（含已停用）。 */
  const adminList = ref<Currency[]>([])
  const adminLoading = ref(false)

  /** 拉取全部币种（含已停用）。 */
  async function listAll(): Promise<Currency[]> {
    adminLoading.value = true
    try {
      const result = await request<Currency[]>(ADMIN_CURRENCIES_PATH)
      adminList.value = result
      return result
    } finally {
      adminLoading.value = false
    }
  }

  /** 新建币种。代码重复时后端返回 409。 */
  async function create(payload: CreateCurrencyPayload): Promise<void> {
    await request<Currency>(ADMIN_CURRENCIES_PATH, { method: 'POST', body: payload })
  }

  /** 修改币种的名称、符号与排序；代码不可修改。 */
  async function update(id: number, payload: UpdateCurrencyPayload): Promise<void> {
    await request<void>(`${ADMIN_CURRENCIES_PATH}/${id}`, { method: 'PUT', body: payload })
  }

  /** 启用或停用币种（停用即软删除，数据行保留）。 */
  async function setActive(id: number, isActive: boolean): Promise<void> {
    await request<void>(`${ADMIN_CURRENCIES_PATH}/${id}/${isActive ? 'activate' : 'deactivate'}`, {
      method: 'POST',
    })
  }

  /** 设为系统默认币种（全系统至多一个，后端在同一事务内清除其余标记）。 */
  async function setDefault(id: number): Promise<void> {
    await request<void>(`${ADMIN_CURRENCIES_PATH}/${id}/set-default`, { method: 'POST' })
  }

  return {
    currencies,
    loading,
    loaded,
    defaultCode,
    loadActive,
    findByCode,
    clear,
    adminList,
    adminLoading,
    listAll,
    create,
    update,
    setActive,
    setDefault,
  }
})
