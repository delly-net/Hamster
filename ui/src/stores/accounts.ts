/**
 * 账户状态。
 *
 * 账户归属且仅归属一个账套，列表按**当前账套**过滤：请求由 `api/http.ts` 自动附带
 * `X-Account-Set-Id`，此处不做账套判断——切换账套后的重新拉取由页面 watch 当前账套负责。
 *
 * 可见性（谁能看见、谁能改）完全由后端判定：本 store 不做任何本地过滤，
 * 篡改本地状态只会拿到一批 403 / 404。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/** 归属范围：个人账户仅归属人可用，公共账户账套内成员共用。 */
export type AccountScope = 'Personal' | 'Public'

/** 账户类型。 */
export type AccountType = 'Ledger' | 'Fund' | 'Liability' | 'Contact'

/** 归属范围的中文标签。 */
export const ACCOUNT_SCOPE_LABELS: Record<AccountScope, string> = {
  Personal: '个人',
  Public: '公共',
}

/**
 * 账户类型的中文标签。
 *
 * 覆盖后端枚举的**全部**取值（是枚举的完整镜像），其中 `Ledger` 不会出现在任何接口返回里：
 * 账本账户由系统在期初入账时自动创建，对任何人不呈现。保留它只为让本表与枚举一一对应。
 */
export const ACCOUNT_TYPE_LABELS: Record<AccountType, string> = {
  Ledger: '账本账户',
  Fund: '资金账户',
  Liability: '负债账户',
  Contact: '往来账户',
}

/** 归属范围下拉选项（顺序即界面呈现顺序）。 */
export const ACCOUNT_SCOPE_OPTIONS: { value: AccountScope; label: string }[] = [
  { value: 'Personal', label: ACCOUNT_SCOPE_LABELS.Personal },
  { value: 'Public', label: ACCOUNT_SCOPE_LABELS.Public },
]

/**
 * 账户类型下拉选项（顺序即界面呈现顺序）。
 *
 * **刻意不含 `Ledger`**：新建与行内编辑两个下拉共用本数组，一处收敛即两处生效。
 * 与 `AccountType` 联合类型 / `ACCOUNT_TYPE_LABELS` 的分工是——那两者描述「接口可能回传什么」
 * （后端枚举的忠实镜像），本数组描述「用户可手工选什么」，故这里少一项不是遗漏。
 * 后端同样会拒绝 `Ledger`（见 `AccountTypeExtensions.IsUserAssignable`），此处少一项只是不让用户白试一次。
 */
export const ACCOUNT_TYPE_OPTIONS: { value: AccountType; label: string }[] = [
  { value: 'Fund', label: ACCOUNT_TYPE_LABELS.Fund },
  { value: 'Liability', label: ACCOUNT_TYPE_LABELS.Liability },
  { value: 'Contact', label: ACCOUNT_TYPE_LABELS.Contact },
]

/**
 * 可作为**转账**转出/转入的账户类型（顺序即界面呈现顺序）。
 *
 * 与 `ACCOUNT_TYPE_OPTIONS` 的分工是——那者描述「用户可手工建立什么账户」（资金/负债/往来），
 * 本数组描述「哪些账户之间可以转账」（只有资金与负债）。往来账户记录的是「谁欠谁」而不是
 * 「钱放在哪」，钱转进转出它并不改变钱的所在，故不能作为转账端点。
 *
 * 这是后端 `AccountTypeExtensions.IsTransferAccount` 的前端镜像：后端会拒绝其余类型（400），
 * 此处少几项只是不让用户先选中再被挡下（与按币种过滤候选同一取舍）。
 */
export const TRANSFER_ACCOUNT_TYPES: readonly AccountType[] = ['Fund', 'Liability']

/** 账户（后端不回传内部明细）。 */
export interface Account {
  id: number
  accountSetId: number
  name: string
  scope: AccountScope
  /** 账户类型；接口**不会回传 `Ledger`**——账本账户由系统创建、对任何人不呈现。 */
  type: AccountType
  /** 归属人主键；公共账户为 `null`。 */
  ownerUserId: number | null
  /** 归属人用户名；公共账户为 `null`。 */
  ownerUsername: string | null
  /** 期初金额；创建后不可修改（已落成一笔期初交易）。 */
  initialBalance: number
  /** 余额；**只读派生值**，等于该账户全部交易明细的有符号汇总。 */
  balance: number
  /** 记账币种代码（如 `CNY`）；创建后不可修改。**只有币种相同的账户之间才能交易**。 */
  currencyCode: string
  /** 是否为系统自动创建的内置账户（当前即期初账本账户）。 */
  isSystem: boolean
  /** 是否启用；`false` 表示已停用（软删除）。 */
  isActive: boolean
  /** 创建时间（UTC，ISO 8601）。 */
  createdAt: string
}

/** 新建账户的入参。 */
export interface CreateAccountPayload {
  name: string
  scope: AccountScope
  type: AccountType
  initialBalance: number
  /** 记账币种代码；必须是**启用的**币种。先在币种选择框中选定，再填其余字段。 */
  currencyCode: string
}

/**
 * 修改账户的入参。
 *
 * **只有名称**：归属范围、归属人、期初金额、账户类型与币种一经创建均不可修改，故都不在其中。
 * 类型不可改的理由是——类型是账户的分类身份，既有流水都按它归类，换类型等于给历史流水换一套解释。
 * 币种不可改的理由同理——它是一切金额的计价单位，换币种会让既有余额变成另一个数。
 * 后端 `PUT` 的请求体同样只有名称，传了类型与币种不会被读取。
 */
export interface UpdateAccountPayload {
  name: string
}

/** 接口基址。 */
const ACCOUNTS_PATH = '/api/accounts'

export const useAccountsStore = defineStore('accounts', () => {
  /** 当前账套内我可见的账户。 */
  const accounts = ref<Account[]>([])
  const loading = ref(false)

  /**
   * 拉取当前账套内我可见的账户。
   *
   * @param includeInactive 是否包含已停用的账户；默认只取启用的。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function list(includeInactive = false): Promise<Account[]> {
    loading.value = true
    try {
      const result = await request<Account[]>(`${ACCOUNTS_PATH}?includeInactive=${includeInactive}`)
      accounts.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /** 新建账户。个人账户的归属人由后端强制为当前登录者，请求体无需指定。 */
  async function create(payload: CreateAccountPayload): Promise<void> {
    await request<Account>(ACCOUNTS_PATH, { method: 'POST', body: payload })
  }

  /** 修改账户名称；类型与期初金额均不可修改（见 `UpdateAccountPayload` 的说明）。 */
  async function update(id: number, payload: UpdateAccountPayload): Promise<void> {
    await request<void>(`${ACCOUNTS_PATH}/${id}`, { method: 'PUT', body: payload })
  }

  /** 启用或停用账户（停用即软删除，数据行保留）。 */
  async function setActive(id: number, isActive: boolean): Promise<void> {
    await request<void>(`${ACCOUNTS_PATH}/${id}/${isActive ? 'activate' : 'deactivate'}`, {
      method: 'POST',
    })
  }

  /** 清空列表（退出登录或账套失效时调用，避免残留上一账套的账户）。 */
  function clear(): void {
    accounts.value = []
  }

  return {
    accounts,
    loading,
    list,
    create,
    update,
    setActive,
    clear,
  }
})
