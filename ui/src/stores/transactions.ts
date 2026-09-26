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
 * **分类挂在交易上、是可选项**：可以留空（「未分类」），也可以只报一个名字由后端自动创建。
 * 一笔转账只带一个分类——「这笔账因何而发生」是整笔的属性，不是某一条明细的。
 * 分类与币种、账户类型都无关，故它不参与上面的任何一条约束。
 *
 * **标签同样挂在交易上、同样可选，但可以有多个**：落在 `hamster_transaction_tag` 子表里。
 * 主键集合（`tagIds`）与名字集合（`tagNames`）**是合并关系而非二选一**——
 * 「从候选里选了一个、又手打了一个」是最常见的用法，二者都上报、由后端合并去重。
 * 这与分类的「有主键就不用名字」刻意不同，且不参与币种与账户类型那几条约束。
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
import type { TagRef } from '@/stores/tags'

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

/**
 * 一种记账类型的界面文案与两个账户端的角色名。
 *
 * 用查找表而非嵌套三元表达式：类型从两种涨到三种后，三元表达式会退化成
 * 「A ? x : B ? y : z」这类读不出对应关系的式子，而这张表把「哪种记账用哪套词」摊平了，
 * 新增类型只需加一行、漏加时 TypeScript 会因 `Record` 缺键当场报错。
 *
 * **定义在 store 而不是记账表单里**：改账弹窗要用同一套角色名（「支出账户」/「目标账户」）。
 * 两处各写一份，同一对端点就会拿到两种叫法，而用户读到的正是这些名字。
 */
export interface TransactionModeMeta {
  /** 记账类型的中文名，用于按钮与提示文案。 */
  label: string
  /** 主账户的字段名。 */
  primaryLabel: string
  /** 对手方账户的字段名。 */
  counterpartyLabel: string
  /** 摘要输入框的占位示例。 */
  summaryPlaceholder: string
}

/**
 * 各记账类型的文案与账户角色。
 *
 * 名字随方向变而非统一叫「账户」：用户看到「收入账户」就知道这里是钱的**落点**，
 * 看到「转出账户」就知道这里是钱的**来处**，两个框的分工无需额外解释。
 */
export const TRANSACTION_MODE_META: Record<RecordableTransactionType, TransactionModeMeta> = {
  Income: {
    label: '收入',
    primaryLabel: '收入账户',
    counterpartyLabel: '来源账户',
    summaryPlaceholder: '如：工资',
  },
  Expense: {
    label: '支出',
    primaryLabel: '支出账户',
    counterpartyLabel: '目标账户',
    summaryPlaceholder: '如：午餐',
  },
  Transfer: {
    label: '转账',
    primaryLabel: '转出账户',
    counterpartyLabel: '转入账户',
    summaryPlaceholder: '如：还信用卡',
  },
}

/**
 * 取某交易类型的文案与账户角色；**非用户可记账的类型返回 `null`**。
 *
 * 期初余额没有「主账户 / 对手方」这两个角色——它的方向由期初金额的符号决定，
 * 不由类型决定，给它编一套名字只会让调用方以为它也能走记账/改账那两条路径。
 * 返回 `null` 而不是抛错：调用方（改账弹窗）本来就只在类型可记账时才被挂载，
 * 这里多给一条分支是为了让「不该出现的类型」当场可见，而不是静默取到一名字。
 */
export function transactionModeMeta(type: TransactionType): TransactionModeMeta | null {
  return type === 'OpeningBalance' ? null : TRANSACTION_MODE_META[type]
}

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
  /**
   * 分类主键；用户从候选中选定分类时为它，未分类时为 `null`。
   *
   * 与 {@link categoryName} 二者**至多传一个**；两者皆空即「未分类」——
   * **这是合法状态**（记账时分类是可选的），后端不会因此报错。
   *
   * 取不到（不属于当前账套）时后端返回 400：分类按账套隔离，越界的主键在库里不该存在。
   */
  categoryId: number | null
  /**
   * 分类名（候选之外的手工输入）；未填或已从候选中选定时为 `null`。
   *
   * 命不中既有分类时后端会**在当前账套内自动创建**它——这正是「手工输入自动创建」的落点。
   * 命中**已停用**的分类时也归到它上面，而不是另建一个同名的。
   *
   * 与对手方不同：分类**任何类型都可按名创建**（转账也不例外），没有账户类型那样的限制。
   */
  categoryName: string | null
  /**
   * 要挂到这笔交易上的标签主键集合；用户从候选中**选中**时进这里，没有标签时传**空数组**。
   *
   * 取不到（不属于当前账套）时后端返回 400：标签按账套隔离，越界的主键在库里不该存在。
   *
   * 与 {@link tagNames} 二者**是合并关系**（各自都可为空，也可同时有值），
   * 两者都空即「没有标签」——**这是合法状态**（记账时标签是可选的）。
   */
  tagIds: number[]
  /**
   * 标签名（候选之外的手工输入）；未填时传**空数组**。
   *
   * 命不中既有标签时后端会**在当前账套内自动创建**它——这正是「手工输入自动创建」的落点。
   * 命中**已停用**的标签时也归到它上面，而不是另建一个同名的。
   *
   * 与 {@link tagIds} 指向同一个标签时按主键去重，只挂一条。
   */
  tagNames: string[]
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
  /** 分类主键；**未分类**时为 `null`。 */
  categoryId: number | null
  /**
   * 分类名称；**未分类**时为 `null`。
   *
   * 手工输入的分类名可能刚被后端自动创建，故提交后要**回显这里的值**而不是输入框里的文本：
   * 只有后端知道那个名字最终归到了哪一条记录上。
   */
  categoryName: string | null
  /**
   * 这笔交易挂着的标签；**没有标签时是空数组**（不是 `null`——标签是多值的，
   * 「没有标签」在界面上的呈现是一片空白，为它写一个判空分支没有信息量）。
   *
   * 手工输入的标签名可能刚被后端自动创建，故提交后要**回显这里的值**而不是输入框里的文本：
   * 与分类同理，只有后端知道那些名字最终归到了哪几条记录上、次序如何。
   */
  tags: TagRef[]
  /** 落库时间（UTC，ISO 8601）。 */
  createdAt: string
}

/**
 * 改账入参。
 *
 * **比记账入参少两个字段，这不是巧合，而是契约上的事实**：
 *
 * - 去掉 `type`：**交易类型不可改**。它决定两条明细的借贷方向，且「收支互改」在语义上
 *   是两笔不同的账。后端为此准备了**另一个请求体记录**（不是「同一份减去两个字段」），
 *   故前端也从类型上删除它——多余字段会被后端静默忽略，留着它只会让「改成功了」变成猜测。
 * - 去掉 `currencyCode`：交易表没有币种列，币种由主账户决定。记账时它是「先选币种再过滤
 *   账户候选」的输入；改账时账户已定，再带一个币种只可能多出「币种与主账户打架」的 400。
 *   币种随主账户走，对手方账户的币种须与之一致（后端校验，与记账同一口径）。
 *
 * 其余字段含义与 {@link RecordTransactionPayload} **逐字相同**（含对手方的三级解析、标签的合并去重
 * 与转账的额外约束）：改一笔账与记一笔账在这些点上是同一件事，后端也共用同一份判定。
 * 唯一的语义差别是**标签与备注一样是覆盖而非保留**：改账提交的 `tagIds`/`tagNames` 代表
 * 这笔交易改完之后**应有的全部标签**，两者都空即「清空标签」。要保留原标签就把它们原样传回来——
 * 与新建时「两者都空即没有标签」在写法上完全一致，故类型上不需要任何区分。
 */
export type UpdateTransactionPayload = Omit<RecordTransactionPayload, 'type' | 'currencyCode'>

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

  /**
   * 修改一笔已记账的交易（收入 / 支出 / 转账）。
   *
   * **改的是整笔交易**：后端会把借贷两条明细一并改写并保持方向不变，故这里只需提交一次；
   * 只改「当前这一行」的接口是不存在的——那会让复式记账的配平当场失效。
   * 账户余额不需要任何额外调用：它是明细的派生值，改完重新查询即得新值。
   *
   * @param id 交易主键（取明细行的 `transactionId`，**不是明细主键**）。
   * @throws 交易不存在、不属于当前账套、或两条明细挂靠的账户不在操作者这一侧时返回 404
   * （三者同响应，不泄露存在性）；期初余额交易返回 400；字段非法、主账户/对手方账户不可见、
   * 转账两端不合规、币种不一致时与记账同响应。
   */
  async function update(id: number, payload: UpdateTransactionPayload): Promise<RecordedTransaction> {
    loading.value = true
    try {
      return await request<RecordedTransaction>(`${TRANSACTIONS_PATH}/${id}`, {
        method: 'PUT',
        body: payload,
      })
    } finally {
      loading.value = false
    }
  }

  return {
    loading,
    record,
    update,
  }
})
