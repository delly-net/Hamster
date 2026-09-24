/**
 * 账目明细状态。
 *
 * 明细一律挂在账套下，请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，此处不做账套判断
 * ——切换账套后的重新查询由页面 watch 当前账套负责（与 `stores/accounts.ts` 同构）。
 *
 * **本 store 不做任何可见性过滤**：后端只返回「挂在我可见账户上」的明细，前端过滤只会造出一道假防线。
 * 账户多选的候选也直接来自 `GET /api/accounts?includeInactive=true`：账户是软删除，停用账户上仍有
 * 历史明细，漏掉它会让过去的账凭空消失。**账本账户不在其中**——后端从不返回它。
 *
 * 行粒度是**一条交易明细**，不是一笔交易：一笔交易涉及两个所选账户时呈现两行。
 * 金额的增减由后端的 `signedAmount` 表达（借方为正、贷方为负），本 store **不折算带符号金额**
 * ——「方向 → 符号」的唯一换算定义在后端的 `EntryDirectionExtensions.SignedAmount`，
 * 前端再算一次就是第二个语义源。`amount` 与 `direction` 仍如实回传，但只作账本的底层事实，
 * 界面按 `signedAmount` 分列呈现。
 *
 * **分类挂在交易上**（不是明细上）：一笔交易的两条明细会拿到同一个 `categoryId` / `categoryName`。
 * 这不是重复，而是「一笔转账只应有一个分类」在明细视图下的如实呈现；未分类时两者均为 `null`，
 * 那是**正常状态**而不是数据缺失。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/**
 * 借贷方向。
 *
 * 界面**不再呈现**它（「借/贷」对普通用户不友好，见 `/entries` 页），但接口仍如实回传，
 * 故类型保留：它是账本的底层事实，也是 `signedAmount` 的来源。
 */
export type EntryDirection = 'Debit' | 'Credit'

/**
 * 交易类型。
 *
 * 与后端枚举一一对应（是枚举的**完整镜像**）：期初余额由系统在账户创建时自动生成，
 * 收入、支出与转账由用户在「收入」「支出」「转账」三个入口手工记账。
 */
export type TransactionType = 'OpeningBalance' | 'Income' | 'Expense' | 'Transfer'

/**
 * 对手方账户相对当前用户的可见性档位。
 *
 * 覆盖后端枚举的**全部**取值。`None` 表示同笔交易里找不到方向相反的明细——当前数据模型下每笔交易
 * 恒有借贷两条，故它**不应出现**；保留它是因为后端确实会回传这个取值，类型里漏掉它会让下面那张
 * 标签表在运行时落空（返回 `undefined` 渲染成空白），比多写一行更糟。
 */
export type CounterpartyKind = 'None' | 'Account' | 'Ledger' | 'Hidden'

/**
 * 交易类型的中文标签。
 *
 * 取值使用处一律经 {@link transactionTypeLabel} 兜底：后端新增类型而前端尚未同步时，
 * 界面显示原字符串而不是空白，问题当场可见且不至于丢信息。
 */
export const TRANSACTION_TYPE_LABELS: Record<TransactionType, string> = {
  OpeningBalance: '期初余额',
  Income: '收入',
  Expense: '支出',
  // 转账在明细页落成两条（转出=支出、转入=收入），这条标签是用户区分
  // 「账户之间的搬运」与「真正的收支」的唯一线索，不可省
  Transfer: '转账',
}

/**
 * 对手方档位的中文占位文案。
 *
 * `Account` 档的文案是空串：该档对手方对当前用户可见，直接呈现 `counterpartyName`，
 * 不需要占位（见 `EntryQueryView.vue` 的 `counterpartyText`）。
 * `Ledger` 显示「账本」：账本账户对任何人不呈现，它是**全部非期初交易的共同对手方**——
 * 期初余额记在它身上，收入与支出也记在它身上。故这里只给一个中性词，
 * 不能再写「期初」：那会把一笔支出的对手方误标成期初。
 * `Hidden` 与 `None` 同为「—」：前者是权限的结论（存在但不可见），后者是数据的问题（没有对手方），
 * 两者都不该给用户任何可辨识信息。
 */
export const COUNTERPARTY_KIND_LABELS: Record<CounterpartyKind, string> = {
  Account: '',
  Ledger: '账本',
  Hidden: '—',
  None: '—',
}

/** 默认每页条数，与后端 `EntryEndpoints.DEFAULT_PAGE_SIZE` 保持一致。 */
export const ENTRY_PAGE_SIZE = 50

/** 一条账目明细（一行 = 一个借贷方向）。 */
export interface Entry {
  id: number
  /** 所属交易主键；同笔交易的两条明细共享它。 */
  transactionId: number
  /** 业务发生时间（UTC，ISO 8601）；可补记往日支出，故与落库时间无关。 */
  occurredAt: string
  /** 交易摘要。 */
  summary: string
  /** 交易备注；无备注时为 `null`。 */
  remark: string | null
  /** 交易类型。 */
  transactionType: TransactionType
  /** 挂靠账户主键（必然是当前用户可见的账户）。 */
  accountId: number
  /** 挂靠账户名称。 */
  accountName: string
  /** 借贷方向；账本的底层事实，界面不再呈现。 */
  direction: EntryDirection
  /** 原始金额，**恒为正**；界面不再呈现它，改用 `signedAmount`。 */
  amount: number
  /**
   * 带符号金额：`amount` 按 `direction` 取符号后的值（借方为正、贷方为负），
   * 含义是「该条明细对该账户余额的增减了多少」。
   *
   * 由后端派生（换算定义 `EntryDirectionExtensions.SignedAmount`，与余额汇总共用一处），
   * 前端据此把金额分入「收入」（正）/「支出」（负）两列并着色。
   */
  signedAmount: number
  /** 对手方账户的可见性档位。 */
  counterpartyKind: CounterpartyKind
  /** 对手方账户主键；**仅 `Account` 档有值**。 */
  counterpartyAccountId: number | null
  /** 对手方账户名称；**仅 `Account` 档有值**。 */
  counterpartyName: string | null
  /**
   * 交易分类主键；**未分类**时为 `null`。
   *
   * 分类**没有可见性档位**（不像对手方那样分 `Account` / `Ledger` / `Hidden`）：
   * 账套内所有成员共用同一份分类字典，没有「他人私有的分类」这一概念，
   * 故后端直接给出主键与名称，此处也不需要对它做任何遮挡。
   */
  categoryId: number | null
  /**
   * 交易分类名称；**未分类**时为 `null`。与 `categoryId` 同生同灭。
   *
   * 名称由后端随行下发（流水挂的是分类主键），故分类改名后历史明细自动显示新名字。
   * **已停用分类的名称照常给出**：停用是「不再供新记账选择」，不是「历史上从未用过」。
   */
  categoryName: string | null
}

/** 查询条件；`from` / `to` 均为 **ISO 8601 UTC** 且为闭区间端点。 */
export interface EntryQueryParams {
  from?: string
  to?: string
  /** 目标账户主键；省略即全部可见账户。不可见的账户会被后端静默剔除（不报错）。 */
  accountIds?: number[]
  page?: number
  pageSize?: number
}

/** 一页明细。 */
export interface EntryQueryPage {
  items: Entry[]
  /** 满足条件的明细总数（跨页），用于呈现总条数与总页数。 */
  total: number
  page: number
  pageSize: number
}

/** 接口基址。 */
const ENTRIES_PATH = '/api/entries'

/** 取交易类型的中文标签；未知取值回退原字符串。 */
export function transactionTypeLabel(type: TransactionType): string {
  return TRANSACTION_TYPE_LABELS[type] ?? String(type)
}

export const useEntriesStore = defineStore('entries', () => {
  /** 最近一次查询结果；`null` 表示尚未查过。 */
  const page = ref<EntryQueryPage | null>(null)
  const loading = ref(false)

  /**
   * 查询账目明细。
   *
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function query(params: EntryQueryParams): Promise<EntryQueryPage> {
    // accountIds 以**重复键**逐个 append（后端绑定为 int[]），不能拼成逗号串
    const search = new URLSearchParams()
    if (params.from !== undefined) {
      search.set('from', params.from)
    }
    if (params.to !== undefined) {
      search.set('to', params.to)
    }
    for (const accountId of params.accountIds ?? []) {
      search.append('accountIds', String(accountId))
    }
    search.set('page', String(params.page ?? 1))
    search.set('pageSize', String(params.pageSize ?? ENTRY_PAGE_SIZE))

    loading.value = true
    try {
      const result = await request<EntryQueryPage>(`${ENTRIES_PATH}?${search.toString()}`)
      page.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /** 清空结果（退出登录、账套切换时调用，避免残留上一账套的明细）。 */
  function clear(): void {
    page.value = null
  }

  return {
    page,
    loading,
    query,
    clear,
  }
})
