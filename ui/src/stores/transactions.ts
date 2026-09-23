/**
 * 记账状态：在当前账套内记一笔收入或支出。
 *
 * 交易归属且仅归属一个账套，请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，此处不做账套判断。
 * 目标账户的可见性完全由后端判定：本 store 不做任何本地过滤，篡改本地状态只会拿到 404。
 *
 * **一笔交易由借贷两条等额反向的明细构成**（收入：目标账户借方 + 系统账本账户贷方；支出反之），
 * 对手方是该账套的系统账本账户——它对任何人不呈现，故本模块不提供也不接收对手方参数。
 * 「方向 → 账户余额」的换算唯一发生在后端 `TransactionService.SumSignedAmountsAsync`，
 * 前端不折算带符号金额，只如实上报「收入还是支出 + 金额」。
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
 */
export type RecordableTransactionType = Extract<TransactionType, 'Income' | 'Expense'>

/** 可由用户记账的交易类型下拉选项（顺序即界面呈现顺序）。 */
export const TRANSACTION_TYPE_OPTIONS: {
  value: RecordableTransactionType
  label: string
}[] = [
  { value: 'Income', label: TRANSACTION_TYPE_LABELS.Income },
  { value: 'Expense', label: TRANSACTION_TYPE_LABELS.Expense },
]

/** 记账入参。 */
export interface RecordTransactionPayload {
  /** 交易类型：`Income` 收入 / `Expense` 支出。 */
  type: RecordableTransactionType
  /** 目标账户主键；收入使其余额增加、支出使其减少。 */
  accountId: number
  /** 金额，**必须大于 0**（方向由 `type` 表达，不用金额符号）。 */
  amount: number
  /** 业务发生时间（UTC，ISO 8601）；可补记往日的收支。 */
  occurredAt: string
  /** 交易摘要，必填。 */
  summary: string
  /** 备注；无备注时传 `null`。 */
  remark: string | null
}

/** 记账成功后的交易（后端只回传「记了哪一笔」，不含明细与对手方）。 */
export interface RecordedTransaction {
  id: number
  accountSetId: number
  /** 交易类型，取值 `Income` / `Expense`。 */
  type: TransactionType
  /** 业务发生时间（UTC，ISO 8601）。 */
  occurredAt: string
  summary: string
  remark: string | null
  /** 本次记账的目标账户主键。 */
  accountId: number
  /** 目标账户名称。 */
  accountName: string
  /** 落库时间（UTC，ISO 8601）。 */
  createdAt: string
}

/** 接口基址。 */
const TRANSACTIONS_PATH = '/api/transactions'

export const useTransactionsStore = defineStore('transactions', () => {
  const loading = ref(false)

  /**
   * 记一笔收入或支出。
   *
   * @throws 未选择账套时后端返回 400；字段非法返回 400；目标账户不可见返回 404；
   * 令牌失效或网络异常时抛出 `ApiError`。
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
