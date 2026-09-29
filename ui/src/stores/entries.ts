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
 *
 * **标签同理，且是多值的**：同一笔交易的两条明细会拿到同一个 `tags` 数组，没有标签时是**空数组**。
 * 数组次序即用户当初提交标签的次序（后端按关联行主键升序取回），界面照此呈现即可、**不必自己排序**。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'
import type { TagRef } from '@/stores/tags'

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

/**
 * 首页「最近交易」面板取多少条。
 *
 * 与 {@link ENTRY_PAGE_SIZE} 是两个用途：那边是「明细页一页看多少」，这边是「首页概览给几条」，
 * 不共用同一个常量——把首页的 10 条挂到明细页的页大小上，改一处的动机会来自两个方向。
 */
export const RECENT_ENTRY_LIMIT = 10

/**
 * 记账页「最近 N 次同类型交易」面板呈现几笔。
 *
 * 「次」是**笔**不是**明细行**：一笔交易恒有借贷两条明细，转账的两端都挂在钱账户上（占两行），
 * 收入/支出的对手方若也是钱账户同样占两行。故这一份结果要先按 `transactionId` 归并再截断，
 * 见 {@link mergeToTransactions}。
 */
export const RECENT_SAME_TYPE_LIMIT = 5

/**
 * 取回多少**明细行**才够归并出 {@link RECENT_SAME_TYPE_LIMIT} 笔交易。
 *
 * **2× 是推导出来的、不是拍脑袋**：同类型的一笔交易在结果里占 1 或 2 行，但**恒 ≥ 1 行**——
 * 收入/支出的主账户、转账的两端都必然是钱账户（主账户候选恒为 `MONEY_ACCOUNT_TYPES`，
 * 转账两端由 `IsTransferAccount()` 限死），故主账户那一行必定出现在结果里。
 * 于是 10 行里至少有 5 笔交易，取前 5 笔一定取满。
 * **改成 5 会让转账页只显示出 2~3 笔**（那两端的行把额度吃掉了）。
 */
export const RECENT_SAME_TYPE_SCAN_SIZE = RECENT_SAME_TYPE_LIMIT * 2

/**
 * 「历史摘要」候选取多少条，与后端 `EntryEndpoints.DEFAULT_SUMMARY_LIMIT` 保持一致。
 *
 * 这是**呈现上限**而不是「够用就好」的估数：摘要去重后的条数远小于记账笔数（同一句写法天天用），
 * 50 条已经覆盖了普通账套的全部写法；要得更多只会让一次请求把整本账的摘要搬回来。
 */
export const SUMMARY_OPTION_LIMIT = 50

/**
 * 历史摘要的一条候选（一个去重后的摘要写法）。
 *
 * 与 {@link Entry} 刻意分开：候选里**没有主键**——摘要不是字典项，库里没有一张「摘要表」，
 * 用户选中它只是把那段文本填进输入框，之后还能接着改。给它一个 id 会凭空造出
 * 「这段文本对应哪一条记录」这个并不存在的问题（同 `EntryRecordForm` 对摘要字段的定位）。
 */
export interface SummaryOption {
  /** 摘要原文。 */
  summary: string
  /** 最近一次使用时间（UTC，ISO 8601）；即该摘要下**业务发生时间**的最大值。 */
  lastUsedAt: string
  /** 用了多少次（**笔数**，不是明细行数）。 */
  usageCount: number
}

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
  /**
   * 这条明细是否挂在**主账户**上（主账户 = 记账时选定的那个账户：收入账户 / 支出账户 / 转出账户）。
   *
   * 一笔交易的两条明细里恰有一行为 `true`（两条明细方向恒相反）；
   * 期初余额的行恒为 `false`——期初没有「主账户」这一概念（它的方向由期初金额的符号决定）。
   *
   * **改账要用它**：一笔交易可能占两行（转账的两端都会呈现），从任一行点开编辑时，
   * 都要把「这一行」还原成「主账户 + 对手方」两个端点——本行为 `true` 则 `accountId` 即主账户，
   * 否则主账户是它的对手方（`counterpartyAccountId`）。
   *
   * **判据由后端下发，前端不镜像**：它是「方向是否等于该类型的主账户方向」，
   * 算错的后果是「编辑写到了错误的账户上」（数据损坏），故与 `signedAmount` 同一取舍
   * ——后端算好给出，前端只消费。
   */
  isPrimary: boolean
  /**
   * 这笔交易挂着的标签；**没有标签时是空数组**（标签挂在交易上，故同笔交易的两条明细拿到同一份）。
   *
   * 次序即用户当初提交的次序，**界面照此呈现即可，不必自己排序**。
   *
   * 标签**没有可见性档位**（与分类同理、与对手方相反）：账套内所有成员共用同一份标签词汇表，
   * 没有「他人私有的标签」这一概念。
   * **已停用标签的名称照常展示**：停用是「不再供新记账选择」，不是「历史上从未用过」——
   * 用户看的是「这笔账当时标了什么」，不是「这份词汇表现在长什么样」，故不下发也不呈现启用状态。
   */
  tags: TagRef[]
}

/** 查询条件；`from` / `to` 均为 **ISO 8601 UTC** 且为闭区间端点。 */
export interface EntryQueryParams {
  from?: string
  to?: string
  /** 目标账户主键；省略即全部可见账户。不可见的账户会被后端静默剔除（不报错）。 */
  accountIds?: number[]
  /**
   * 目标标签主键；省略即不限标签。
   *
   * 匹配语义是「**任一命中**」而非「全部命中」——多选标签的常规意图是「这几类我都想看看」，
   * 后端据此实现，前端不要另行收窄。
   *
   * 与 `accountIds` 一样，不属于当前账套的标签主键会被后端静默忽略（不报错、只是匹配不到）；
   * 与 `accountIds` **同时给出时是「且」的关系**，两个维度各自收窄。
   */
  tagIds?: number[]
  /**
   * 目标交易类型；省略即不限类型。
   *
   * 与 `accountIds` / `tagIds` 一样以**重复键**上报（后端绑定为字符串数组、按枚举名解析）。
   * 后端对未知取值返回 400（写 `errors.types`）而不是静默忽略——`TransactionType` 是后端枚举的
   * 完整镜像，正常不会传错。
   *
   * **按类型筛选必须走后端**：本地筛同一页数据会被该账套里大量其它类型的明细挤空。
   */
  types?: TransactionType[]
  page?: number
  pageSize?: number
  /**
   * 排序方向：`asc`（升序，**省略即此**）或 `desc`（倒序）。
   *
   * 省略时**不往查询串里写**这个参数，与后端「未指定即升序」的默认保持一致——
   * 明细页因此完全不受本参数影响（它的行为与本参数引入前逐字相同）。
   * 倒序是三级排序键**逐级反向**（后端实现），即升序结果的严格逆序，翻页同样稳定。
   */
  order?: 'asc' | 'desc'
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

/**
 * 该行是否可编辑——**决定界面上是否呈现编辑入口**。
 *
 * 两个条件，与后端 `PUT /api/transactions/{id}` 的准入条件**同源**：
 *
 * 1. **类型可由用户记账**：期初余额由系统在账户创建时生成，它的金额恒等于账户的期初余额、
 *    且每账户至多一条，改它会让这两条不变量同时失效（后端 400）。
 * 2. **对手方档位为 `Account` 或 `Ledger`**：`Ledger` 是系统账本账户，它对任何人不呈现，
 *    却是每一笔收支的合法对手方，照常可编辑；`Hidden` 表示对手方是**当前用户看不见的账户**
 *    ——那笔账的另一半在别人名下，改它等于替别人改账（后端 404）；`None` 是数据问题（没有对手方），
 *    连对手方都定不出来，自然也谈不上「同步改写两条关联明细」。
 *
 * **不可编辑时不出按钮，而不是出禁用态按钮**：禁用控件占位会让用户去猜「为什么不能点」，
 * 而这里的两个原因（系统生成、别人名下的账户）都不是用户在当前页面能解决的。
 *
 * 判据放这里一处，供页面与验证共用——写成两份的话，界面上的入口与后端的准入迟早对不上。
 */
export function isEntryEditable(entry: Entry): boolean {
  return (
    entry.transactionType !== 'OpeningBalance' &&
    (entry.counterpartyKind === 'Account' || entry.counterpartyKind === 'Ledger')
  )
}

/**
 * 把「明细行」收敛成「一笔一行」：按 `transactionId` 保序去重，每笔取**主账户那一行**。
 *
 * 记账页的「最近 N 次同类型交易」要的是**笔**，而接口的行粒度是**明细**——一笔交易恒有借贷两条，
 * 两条都挂在钱账户上时会各占一行（转账必然如此）。故必须先归并，否则「5 次」会变成「2.5 笔」。
 *
 * **保序**：入参已由后端按 `order=desc` 排好，`Map` 的插入序即「每笔首次出现」的顺序，
 * 故 `[...map.values()]` 就是「最新的在前」，不需要也不应该再排一次。
 *
 * **取哪一行**：`isPrimary === true` 的那行（主账户行）——它就是用户记账时选定的那个账户，
 * 「主账户 → 对手方」的读法才成立。判据来自后端下发的字段，**本函数不按方向、也不按金额正负去猜**
 * （#58 已定：该判据只定义在后端一处，前端镜像它等于把「算错就写错账户」的逻辑搬进来）。
 *
 * 该笔没有 `isPrimary` 行时退回它的首行——当前数据模型下不该发生（期初没有主账户这一概念，
 * 而期初不会出现在记账页的面板里），这一支是**防御**而不是分支：宁可显示一行不够精确的明细，
 * 也不要让这一笔在面板上凭空消失。
 *
 * @param items 明细行（按后端给的方向排好）。
 * @returns 一笔一行、最新的在前。
 */
export function mergeToTransactions(items: Entry[]): Entry[] {
  const byTransaction = new Map<number, Entry>()
  for (const item of items) {
    const existing = byTransaction.get(item.transactionId)
    if (existing === undefined) {
      byTransaction.set(item.transactionId, item)
      continue
    }

    // 同一笔的第二行（对手方行）到达：只有它才是主账户行时才替换掉先到的那一行
    if (!existing.isPrimary && item.isPrimary) {
      byTransaction.set(item.transactionId, item)
    }
  }

  return [...byTransaction.values()]
}

export const useEntriesStore = defineStore('entries', () => {
  /** 明细页那份最近一次查询结果；`null` 表示尚未查过。 */
  const page = ref<EntryQueryPage | null>(null)
  const loading = ref(false)

  /**
   * 首页「最近交易」面板的那一份结果；`null` 表示尚未查过。
   *
   * **与上面那份刻意分开，不共用 `page` / `loading`**：两者打的是同一个端点，但参数集没有一处相同
   * ——明细页是「时间区间 + 账户多选 + 标签多选 + `pageSize=50` + 升序 + 分页」，
   * 首页是「不筛选 + `pageSize=10` + 倒序 + 恒第 1 页」。共用一个结果槽会让首页那 10 条
   * 把明细页的分页结果顶掉；共用一个 `loading` 会让首页的一次刷新把明细页的【查询】按钮变灰。
   * 两者唯一值得共用的东西是**打哪个 URL**，那一层收在下面的 `fetchPage` 里。
   */
  const recentItems = ref<Entry[] | null>(null)

  /** 首页那一份的总条数（当前账套内的明细总数）；只用于呈现，首页不翻页。 */
  const recentTotal = ref(0)

  /** 首页那一份是否在途；与 `loading` 分开，理由见 `recentItems`。 */
  const recentLoading = ref(false)

  /**
   * 记账页「最近 N 次同类型交易」的那一份结果；`null` 表示尚未查过。
   *
   * **又是独立的一份，理由与前两份相同**：它与 {@link recentItems} 打的是同一个端点，但参数多一个
   * `types`，且结果要先归并成「一笔一行」。共用结果槽会让记账页那一份把首页那 10 条顶掉
   * （两页可能同时挂载往返切换），共用 `loading` 会让记账页的一次刷新把首页的加载态点亮。
   */
  const recentSameTypeItems = ref<Entry[] | null>(null)

  /** 记账页那一份是否在途；与另外两个分开，理由见 `recentSameTypeItems`。 */
  const recentSameTypeLoading = ref(false)

  /**
   * 「历史摘要」候选的那一份结果；`null` 表示尚未取回。
   *
   * **又是独立的一份**：它打的是 `/api/entries/summaries`，与上面三份**不是同一个端点**
   * （那三个是明细查询，这个是按摘要分组的聚合），共用结果槽会让两者互相顶掉。
   * 两处调用方（记账表单 `EntryRecordForm` 与改账弹窗 `EntryEditDialog`）不会同时挂载
   * ——弹窗只出现在明细页、表单只出现在三个记账页，故一份槽够用；
   * 切换记账类型（收入→支出）时由 `loadSummaries` 的调用方先 `clearSummaryOptions`，
   * 不留下上一种类型的候选（收入的历史摘要出现在支出页是实打实的错数据，不是旧数据）。
   */
  const summaryOptions = ref<SummaryOption[] | null>(null)

  /** 摘要候选是否在途；与另外三个分开，理由见 `summaryOptions`。 */
  const summaryOptionsLoading = ref(false)

  /** 发一次明细查询并解出分页结果；**URL 拼装的唯一落点**，两个入口共用。 */
  async function fetchPage(search: URLSearchParams): Promise<EntryQueryPage> {
    return request<EntryQueryPage>(`${ENTRIES_PATH}?${search.toString()}`)
  }

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
    // tagIds 同 accountIds：**重复键**逐个 append，不能拼成逗号串
    for (const tagId of params.tagIds ?? []) {
      search.append('tagIds', String(tagId))
    }
    // types 同上：**重复键**逐个 append，值是枚举名（后端按名称白名单解析）
    for (const type of params.types ?? []) {
      search.append('types', type)
    }
    search.set('page', String(params.page ?? 1))
    search.set('pageSize', String(params.pageSize ?? ENTRY_PAGE_SIZE))
    // 未传即不写这个参数：后端「未指定 = 升序」的默认与明细页的既有行为逐字一致
    if (params.order !== undefined) {
      search.set('order', params.order)
    }

    loading.value = true
    try {
      const result = await fetchPage(search)
      page.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /**
   * 取首页「最近交易」要的那几条：**最新在前**的 {@link RECENT_ENTRY_LIMIT} 条明细。
   *
   * 不带任何筛选：首页给的是「这个账套最近发生了什么」，不是「我上次筛出来的那一段里最近发生了什么」。
   * **行过滤由后端承担**（只返回资金/负债账户上的明细，与明细页同一口径），这里不再过滤一次
   * ——前端过滤只会造出一道与后端不一致的假防线。
   *
   * 刻意**不复用 {@link query}**：那会把 `pageSize=10`、倒序、不筛选这几个参数塞给明细页的槽。
   *
   * @returns 最新的若干条明细（可能为空数组）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadRecent(): Promise<Entry[]> {
    const search = new URLSearchParams()
    // 倒序 + 第 1 页 = 最新的若干条；`total` 仍是**全部**明细的条数（后端按筛选条件计数，与方向无关）
    search.set('order', 'desc')
    search.set('page', '1')
    search.set('pageSize', String(RECENT_ENTRY_LIMIT))

    recentLoading.value = true
    try {
      const result = await fetchPage(search)
      recentItems.value = result.items
      recentTotal.value = result.total
      return result.items
    } finally {
      recentLoading.value = false
    }
  }

  /**
   * 取记账页要的「最近 {@link RECENT_SAME_TYPE_LIMIT} 次**同类型**交易」。
   *
   * 类型筛选由后端承担（{@link RECENT_SAME_TYPE_SCAN_SIZE} 里写了为什么必须如此），归并由
   * {@link mergeToTransactions} 在本地完成——**扫描量刻意大于呈现量**，两者不共用一个常量。
   *
   * 刻意**不复用 {@link query} / {@link loadRecent}**：三者的参数集没有一处相同，
   * 共用会把结果塞进别人那一份的槽里。
   *
   * @param type 记账类型（`Income` / `Expense` / `Transfer`）。
   * @returns 最新的若干**笔**交易，每笔一行（可能为空数组）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadRecentSameType(type: TransactionType): Promise<Entry[]> {
    const search = new URLSearchParams()
    // 倒序 + 第 1 页：取最新的一批明细行，再在本地归并成笔
    search.set('order', 'desc')
    search.set('page', '1')
    search.set('pageSize', String(RECENT_SAME_TYPE_SCAN_SIZE))
    search.append('types', type)

    recentSameTypeLoading.value = true
    try {
      const result = await fetchPage(search)
      const merged = mergeToTransactions(result.items).slice(0, RECENT_SAME_TYPE_LIMIT)
      recentSameTypeItems.value = merged
      return merged
    } finally {
      recentSameTypeLoading.value = false
    }
  }

  /**
   * 取「历史摘要」候选：本账套内该类型**用过的摘要**，去重后按最近使用时间倒序。
   *
   * 去重与排序**都在后端**（那是 `GROUP BY`，见 `GET /api/entries/summaries` 的说明）：
   * 本地拿明细去重只能覆盖取回的那一页，「上个月记过的摘要」会凭空不在候选里。
   *
   * 类型过滤同样落在服务端，且**必须**如此：它与记账页的「最近 N 次同类型交易」同一口径
   * ——收入页列出的应当是收入的历史摘要（「工资」），而不是本账套所有类型混在一起的长列表。
   *
   * 刻意**不复用 {@link query} / {@link loadRecent} / {@link loadRecentSameType}**：
   * 前两者打的是明细查询端点（结果与分页），后者虽然也是「同类型」，但结果是**明细行**、
   * 还要在本地归并成笔；本方法要的是另一个端点上的另一种形状。
   *
   * @param type 记账类型（`Income` / `Expense` / `Transfer`）。
   * @returns 去重后的候选（可能为空数组）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadSummaries(type: TransactionType): Promise<SummaryOption[]> {
    const search = new URLSearchParams()
    search.append('types', type)
    search.set('limit', String(SUMMARY_OPTION_LIMIT))

    summaryOptionsLoading.value = true
    try {
      const result = await request<SummaryOption[]>(
        `${ENTRIES_PATH}/summaries?${search.toString()}`,
      )
      summaryOptions.value = result
      return result
    } finally {
      summaryOptionsLoading.value = false
    }
  }

  /** 清空结果（退出登录、账套切换时调用，避免残留上一账套的明细）。 */
  function clear(): void {
    page.value = null
  }

  /** 清空首页那一份（与 {@link clear} 分开：两个页面的生命周期互不相干）。 */
  function clearRecent(): void {
    recentItems.value = null
    recentTotal.value = 0
  }

  /** 清空记账页那一份；理由与 {@link clearRecent} 相同。 */
  function clearRecentSameType(): void {
    recentSameTypeItems.value = null
  }

  /** 清空摘要候选；理由与 {@link clearRecent} 相同（换账套、退出登录，以及切换记账类型时）。 */
  function clearSummaryOptions(): void {
    summaryOptions.value = null
  }

  return {
    page,
    loading,
    query,
    clear,
    recentItems,
    recentTotal,
    recentLoading,
    loadRecent,
    clearRecent,
    recentSameTypeItems,
    recentSameTypeLoading,
    loadRecentSameType,
    clearRecentSameType,
    summaryOptions,
    summaryOptionsLoading,
    loadSummaries,
    clearSummaryOptions,
  }
})
