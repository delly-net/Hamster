/**
 * 记账状态：在当前账套内记一笔收入、支出或转账。
 *
 * 交易归属且仅归属一个账套，请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，此处不做账套判断。
 * 目标账户的可见性完全由后端判定：本 store 不做任何本地过滤，篡改本地状态只会拿到 404。
 *
 * **一笔交易由借贷两条等额反向的明细构成**（收入：收入账户借方 + 对手方贷方；支出与转账反之）。
 * 对手方可以由用户指定——不给就是该账套内**该币种**的系统账本账户，语义是「款项来自/去往账套之外」；
 * 给了则是一笔两个真实账户之间的资金转移。指向账本账户时它对任何人不呈现，故本模块只上报用户填的那个。
 *
 * **转账（`type: 'Transfer'`）是同一个端点的第三种形态**：对手方（转入账户）必填、不落账本账户、
 * 两端都必须是资金或负债账户，这几条约束全在后端判定；本 store 只如实上报两个账户主键。
 * 前端按账户类型过滤候选只是体验层的提前收敛，不是防线。
 * 「方向 → 账户余额」的换算唯一发生在后端 `TransactionService.SumSignedAmountsAsync`，
 * 前端不折算带符号金额，只如实上报「记的是哪一种 + 金额」。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'
import { TRANSACTION_TYPE_LABELS, type TransactionType } from '@/stores/entries'

/**
 * 可由用户手工记账的交易类型。
 *
 * 对应后端 `TransactionTypeExtensions.IsUserRecordable`，**刻意不含 `OpeningBalance`**：
 * 期初余额由系统在账户创建时自动生成，后端会拒绝手工记一笔期初。
 *
 * 三种类型**共用同一个端点**（后端如此，前端也如此）：转账与收支的落库动作完全相同，
 * 差异只在账户角色与几处校验，故不另开端点或另写一份提交逻辑。
 */
export type RecordableTransactionType = Extract<TransactionType, 'Income' | 'Expense' | 'Transfer'>

/** 可由用户记账的交易类型下拉选项（顺序即界面呈现顺序）。 */
export const TRANSACTION_TYPE_OPTIONS: {
  value: RecordableTransactionType
  label: string
}[] = [
  { value: 'Income', label: TRANSACTION_TYPE_LABELS.Income },
  { value: 'Expense', label: TRANSACTION_TYPE_LABELS.Expense },
  { value: 'Transfer', label: TRANSACTION_TYPE_LABELS.Transfer },
]

/** 记账入参。 */
export interface RecordTransactionPayload {
  /** 交易类型：`Income` 收入 / `Expense` 支出 / `Transfer` 转账。 */
  type: RecordableTransactionType
  /**
   * 主账户主键；收入（收入账户）使其余额增加，支出（支出账户）与转账（**转出账户**）使其减少。
   */
  accountId: number
  /** 金额，**必须大于 0**（方向由 `type` 表达，不用金额符号）。 */
  amount: number
  /** 业务发生时间（UTC，ISO 8601）；可补记往日的收支。 */
  occurredAt: string
  /** 交易摘要，必填。 */
  summary: string
  /** 备注；无备注时传 `null`。 */
  remark: string | null
  /**
   * 币种代码，必填。**须与两个账户的币种一致**，后端会因此校验并把不一致拒成 400。
   *
   * 账户上已经带了币种，此处仍要上报：币种是这笔金额的组成部分（「100」离开单位没有意义），
   * 让它由用户显式选定，比在提交时从账户里悄悄取一个更清楚。
   */
  currencyCode: string
  /**
   * 对手方账户主键；用户没有从候选中选定任何账户时为 `null`。
   *
   * 与 {@link counterpartyName} 二者**至多传一个**：两者皆空即「未指定对手方」，
   * 后端落回该币种的系统账本账户。
   *
   * **转账时它必填且语义为「转入账户」**：转账的两端都是真实账户，没有「账套之外」这一说，
   * 后端对留空返回 400。
   */
  counterpartyAccountId: number | null
  /**
   * 对手方账户名（候选之外的手工输入）；未填或已从候选中选定时为 `null`。
   *
   * 命不中既有可见账户时后端会**自动创建为个人往来账户**（期初金额 0）。
   *
   * **转账时恒为 `null`**：按名新建出来的是往来账户，而转账只允许资金与负债账户，
   * 后端对非空取值返回 400。
   */
  counterpartyName: string | null
}

/** 记账成功后的交易（后端只回传「记了哪一笔」，不含明细）。 */
export interface RecordedTransaction {
  id: number
  accountSetId: number
  /** 交易类型，取值 `Income` / `Expense` / `Transfer`。 */
  type: TransactionType
  /** 业务发生时间（UTC，ISO 8601）。 */
  occurredAt: string
  summary: string
  remark: string | null
  /** 本次记账的主账户主键；转账时为转出账户主键。 */
  accountId: number
  /** 主账户名称；转账时为转出账户名称。 */
  accountName: string
  /** 本次记账的币种代码。 */
  currencyCode: string
  /**
   * 对手方账户名称；**未指定对手方（落到系统账本账户）时为 `null`**。
   *
   * 账本账户对任何人不呈现，后端不会把它的名字回传。**转账时它是转入账户名称，恒有值**。
   */
  counterpartyName: string | null
  /** 落库时间（UTC，ISO 8601）。 */
  createdAt: string
}

/** 接口基址。 */
const TRANSACTIONS_PATH = '/api/transactions'

export const useTransactionsStore = defineStore('transactions', () => {
  const loading = ref(false)

  /**
   * 记一笔收入、支出或转账。
   *
   * @throws 未选择账套时后端返回 400；字段非法或**两个账户币种不一致**返回 400；
   * 转账未指定转入账户、两端之一不是资金/负债账户、两端是同一个账户返回 400；
   * 主账户或对手方账户不可见返回 404；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function record(payload: RecordTransactionPayload): Promise<RecordedTransaction> {
    loading.value = true
    try {
      return await request<RecordedTransaction>(TRANSACTIONS_PATH, {
        method: 'POST',
        body: payload,
      })
    } finally {
      loading.value = false
    }
  }

  return {
    loading,
    record,
  }
})
