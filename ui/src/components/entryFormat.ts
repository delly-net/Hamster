/**
 * 账目明细的**呈现助手**：金额格式化、对手方/分类/标签文案、时间格式化。
 *
 * 这些函数原先长在 `views/EntryQueryView.vue` 里，首页的「最近交易」面板也要用其中的金额、对手方、
 * 分类、标签与时间五项，故提到此处**一处定义、两页共用**（与 `components/trendChart.ts` 同属
 * 「呈现层共用模块」，且 `formatAmount` 也正是由首页折线图的刻度与提示气泡取用）。
 *
 * **不得在页面里各写一份**：这些函数里有三条已经确认过的全站口径，抄一份就等于立了第二个真源：
 *
 * - 金额缺失必须**显式呈现**（{@link AMOUNT_UNAVAILABLE}），不允许静默留空或渲染成 `NaN`
 *   （#44：三列表的全部语义都建立在「空白 vs 有值」上，留空会把「字段没取到」呈现成
 *   「这行真的没有收支」）。
 * - 两位小数**只有一个落点**（{@link formatAmount}），带符号与去符号两种写法都由它派生（#54）。
 * - `CounterpartyKind.Ledger` 的文案是「账本」（{@link COUNTERPARTY_KIND_LABELS}）而不是「期初」——
 *   账本账户同时是期初与每一笔收支的对手方，写成「期初」会把一笔支出的对手方标成期初。
 *   这类改名漏改一处，界面上就会出现两种叫法。
 *
 * 本模块**只做呈现**：不折算带符号金额（那是后端 `EntryDirectionExtensions.SignedAmount` 的职责，
 * 前端只消费 `signedAmount`），也不做任何可见性判断。
 */

import { COUNTERPARTY_KIND_LABELS, type Entry } from '@/stores/entries'

/** 金额呈现：固定两位小数，与后端的 `decimal(...,2)` 对齐。 */
const amountFormatter = new Intl.NumberFormat('zh-CN', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/** 后端回传的是带 `Z` 的 UTC 时间，交给 `Intl` 按浏览器本地时区呈现。 */
const dateTimeFormatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'short',
})

/**
 * 金额字段缺失时单元格显示的标记文案。
 *
 * 取值刻意**不像一个金额、也不像一个正常占位符**（页内其余占位一律是 `—`）：
 * 它要让人一眼看出「这不是数据，是异常」，而不是被当成空值的另一种写法。
 */
export const AMOUNT_UNAVAILABLE = '金额异常'

/**
 * 格式化金额：固定两位小数、千分位（**两位小数的唯一落点**）。
 *
 * {@link formatSigned} 与 {@link formatUnsigned} 都经由它出数，故三者的小数口径不会分叉。
 *
 * @param value 金额。
 * @returns 金额文本；非有限数原样回显（调用方应先用 {@link hasSignedAmount} 判定，
 * 需要「金额异常」标记时用下面两个函数）。
 */
export function formatAmount(value: number): string {
  return Number.isFinite(value) ? amountFormatter.format(value) : String(value)
}

/**
 * 本行的 `signedAmount` 是否是一个可用的数。
 *
 * 后端未返回该字段时它是 `undefined`（例如后端进程未按最新代码重建）。
 * 这个判定**必须挡在分列、着色与合计之前**：`undefined > 0` 与 `undefined < 0` 同为 `false`，
 * 一漏过去收入、支出两列就会**双双留空**，或让一个缺失值被判成收入而着绿——两者看起来都像正常数据。
 *
 * @param entry 一条明细。
 * @returns 字段存在且为有限数时返回 `true`。
 */
export function hasSignedAmount(entry: Entry): boolean {
  return Number.isFinite(entry.signedAmount)
}

/**
 * 格式化**带符号**金额：正数补 `+`、负数用 `-`，两者共用同一套两位小数口径。
 *
 * 符号手工拼接而非交给 `Intl`（它对正数不出 `+`），且用的是 ASCII 连字符，
 * 让一列里的正负金额在等宽字体下纵向对齐。
 *
 * **用在「一格容纳正负两种结果」的位置**（明细页的净额列与窄屏卡片、首页「最近交易」的金额列）：
 * 符号是那里表达方向的通道。明细页的收入/支出两列改用 {@link formatUnsigned}——
 * 方向已由列头与本列语义色表达，再补符号是重复。
 *
 * @param value 带符号金额（后端 `signedAmount`）。
 * @returns 带正负号的金额文本；非有限数返回 {@link AMOUNT_UNAVAILABLE} 而非 `String(value)`
 * ——后者会把 `undefined` / `NaN` 原样渲染进表格，看起来像数据。
 */
export function formatSigned(value: number): string {
  if (!Number.isFinite(value)) {
    return AMOUNT_UNAVAILABLE
  }

  return `${value < 0 ? '-' : '+'}${formatAmount(Math.abs(value))}`
}

/**
 * 格式化**不带符号**金额：一律取绝对值，与 {@link formatSigned} 共用同一套两位小数口径。
 *
 * 供明细页的**收入列与支出列**使用：这两列的列头已经说明了方向，同一行又只有一列有值，
 * 再补一个 `+` / `-` 就是第三遍重复。**净额列与首页的金额列不得改用它**：
 * 那两处是「一格同时容纳正负两种结果」的位置，符号是它们表达方向的主要通道。
 *
 * 负号只是被**显示**掉了，不是被抹掉：金额本身仍取自后端带符号的 `signedAmount`，
 * 分列判据也仍是它的正负（见明细页的 `incomeText` / `expenseText`）。
 *
 * @param value 带符号金额。
 * @returns 绝对值文本；非有限数返回 {@link AMOUNT_UNAVAILABLE}——**判定必须先于 `Math.abs()`**，
 * 否则 `Math.abs(undefined)` 得到 `NaN` 并被渲染成 `NaN` 文本（同 #44 的加固口径）。
 */
export function formatUnsigned(value: number): string {
  if (!Number.isFinite(value)) {
    return AMOUNT_UNAVAILABLE
  }

  return formatAmount(Math.abs(value))
}

/** 只到日的格式化：用在「哪一天」本身就是重点、时分只是噪音的位置（如摘要候选行的「最近使用」）。 */
const dateFormatter = new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium' })

/** 格式化 ISO 时间；无法解析时原样回显。 */
export function formatDateTime(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : dateTimeFormatter.format(parsed)
}

/**
 * 格式化 ISO 时间的**日期部分**（本地时区）；无法解析时原样回显。
 *
 * 与 {@link formatDateTime} 共用同一批 `Intl` 口径（同一时区、同一中文格式），只是不带时分。
 * 摘要候选行要回答的是「这条摘要上次是哪天用的」，精确到分既回答不了这个问题，
 * 又会把一行候选挤满——而两个函数各建一个 `Intl` 实例则是第二份时间口径的开端。
 */
export function formatDate(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : dateFormatter.format(parsed)
}

/**
 * 对手方呈现文案。
 *
 * `Account` 档直接给可见账户的名称（该档的标签是空串，见 `COUNTERPARTY_KIND_LABELS`）；
 * 其余档位一律给占位文案：`Ledger` 显示「账本」（**不是「期初」**——账本账户是每一笔收支的
 * 共同对手方，写成「期初」会把一笔支出标成期初），`Hidden` 与 `None` 显示「—」。
 * 后端不会为不可见对手方回传主键与名称，故这里也不存在「回退到 Id」的分支。
 */
export function counterpartyText(entry: Entry): string {
  if (entry.counterpartyKind === 'Account') {
    return entry.counterpartyName ?? COUNTERPARTY_KIND_LABELS.Hidden
  }

  return COUNTERPARTY_KIND_LABELS[entry.counterpartyKind]
}

/**
 * 分类呈现文案：未分类时显示 `—`。
 *
 * 分类名由后端随行下发（流水挂的是分类主键，不是名称），故分类改名后此处自动显示新名字；
 * **已停用分类的名称照常显示**——停用是「不再供新记账选择」，不是「历史上从未用过」。
 * 未分类（`null`）是**正常状态**（记账时分类可选），不是数据缺失，故用与备注同样的 `—` 占位。
 */
export function categoryText(entry: Entry): string {
  return entry.categoryName ?? '—'
}

/**
 * 标签呈现文案：**顿号连接**，没有标签时显示 `—`。
 *
 * 用纯文本而不是一排小徽标：它与【分类】列同属「这笔账的标注」，两处并排时读法应当一致
 * （一列是徽标、一列是文字，会让人以为两者不是同一类东西）；且标签数量不设上限，
 * 徽标在窄列里会挤成一片。顿号正是中文里列举的写法，读起来就是「标了这几个」。
 *
 * 不改名、不过滤：**已停用标签的名称照常显示**（同分类列口径）；次序即用户当初提交的次序，
 * 后端已按此下发，调用方不再排序。标签是交易级属性，一笔交易的两条明细显示同一份，这不是重复。
 */
export function tagText(entry: Entry): string {
  return entry.tags.length === 0 ? '—' : entry.tags.map((tag) => tag.name).join('、')
}

/**
 * 金额单元格的语义色类名。
 *
 * @param value 带符号金额。
 * @param side 该单元格归属的位置：`income` 列只在正数时着绿，`expense` 列只在负数时着红，
 * `net`（「一格容纳正负两种结果」的位置：明细页的净额列、首页的金额列）两向都着色。
 * @returns 语义色类名（`income` / `expense` / `amount-unknown` / 空串）。**非有限数返回告警类**
 * ——不能让它落进 `expense` 分支（`NaN < 0` 为 `false`，会被判成收入而着绿，
 * 等于把一个缺失值标成正常收入）。
 *
 * 类名是两页共用的**词汇**，各自的 `<style scoped>` 里都要有这三个类（配色取自同一批语义令牌：
 * 收入绿 / 支出红 / 危险色）。
 */
export function signedCellClass(value: number, side: 'income' | 'expense' | 'net'): string {
  if (!Number.isFinite(value)) {
    return 'amount-unknown'
  }

  if (side === 'income') {
    return value > 0 ? 'income' : ''
  }

  if (side === 'expense') {
    return value < 0 ? 'expense' : ''
  }

  return value < 0 ? 'expense' : 'income'
}
