<script setup lang="ts">
/**
 * /entries 页面：当前账套内的账目明细查询。
 *
 * 明细一律挂在账套下，故页面严格跟随「当前账套」：未选择账套时只提示、不渲染筛选区与表格，
 * 账套切换时清空结果、重拉账户筛选列表并复位为默认条件（否则会残留上一账套的明细）。
 *
 * **一行是一条交易明细**，不是一笔交易：一笔交易涉及两个所选账户时呈现两行。
 * 金额按「收入 / 支出 / 净额」三列呈现，增减由后端回传的 `signedAmount` 表达（借方为正、贷方为负）
 * ——本页不折算带符号金额，「方向 → 符号」的唯一换算定义在后端。表格底部按当页给出小计。
 *
 * 「借/贷」这一层刻意不呈现给用户：复式记账是数据的组织方式，不是普通人读账的方式。
 *
 * 筛选条件分「草稿」与「已应用」两份：改动输入不立刻发请求（避免边打字边查询），
 * 点【查询】才把草稿落成已应用条件；翻页复用已应用条件，不会因草稿被改动而查错页。
 *
 * 「分类」列取自交易（不是明细）：同一笔交易的两条明细显示同一个分类，未分类显示 `—`。
 *
 * 金额字段（`signedAmount`）缺失时**不允许静默留空**：三列表的全部语义都建立在「空白 vs 有值」上，
 * 留空会把「字段没取到」呈现成「这行真的没有收支」。故缺失一律换成可见的 {@link AMOUNT_UNAVAILABLE}
 * 标记并附一行说明（见 `hasAmountAnomaly`）。
 *
 * 本页不做任何可见性过滤：后端只返回「挂在我可见账户上」的明细，前端过滤只会造出一道假防线。
 * 账户多选的候选含**已停用账户**（软删除后历史明细仍在，漏掉它们会让过去的账凭空消失），
 * 并排除账本账户——它是系统内部账户，后端从不返回它，本页无需为此写过滤逻辑。
 *
 * **窄屏（<1024px）呈现为卡片流**，表格只在宽屏呈现：10 列表格在手机上必须左右拖动才看得见金额，
 * 等于「查得到但读不了」。两份 DOM 并存，由 `display` 在 `App.vue` 定的 1023px 断点上切换
 * ——断点值全站只有这一个，页面级适配不得另起第二套，也不得改用 `matchMedia` 侦测。
 *
 * 这里的 `display: none` 是**双呈现手段，不是死代码**，与「禁止用 CSS 隐藏代替删除」的既有口径不冲突：
 * 后者禁止的是把**同一份内容里多余的字段**藏起来（那会留下多余语义与死代码），
 * 而此处隐藏的是**断点不适用时的整块呈现**——窄屏藏表格、宽屏藏卡片，任一时刻恰有一份进入无障碍树
 * （`display: none` 的节点已从无障碍树移除），不存在重复朗读。`App.vue` 的 `.account` / `.drawer-account`
 * 是同一做法的先例。
 *
 * **卡片头部只给一个带符号金额**（即该行的净额），不再分收入/支出/净额三格：单行视角下三列并不携带
 * 三份信息——收入与支出同格只有一个非空，而净额恒等于那个非空值；三列只有**在合计时**才分道扬镳，
 * 故三项齐备的只有宽屏 `<tfoot>` 与窄屏汇总条（同取自 {@link pageTotals}）。同理卡片**不呈现序号**：
 * 序号是页内行号，无表头可参照时它既不参与定位也不参与计数。
 *
 * 「收入 / 支出」文字标签由金额正负派生（见 {@link sideText}），**不取自 `direction`**：它只是把窄屏下
 * 丢失的「金额在哪一列」这一层信息补回来，与宽屏「按符号分列」同一口径；写成 `direction` 分支等于把
 * 「借/贷」放回页面，也会与既有口径分叉。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import { useAccountSetsStore } from '@/stores/accountSets'
import { useAccountsStore } from '@/stores/accounts'
import {
  COUNTERPARTY_KIND_LABELS,
  ENTRY_PAGE_SIZE,
  transactionTypeLabel,
  useEntriesStore,
  type Entry,
} from '@/stores/entries'

const accountSets = useAccountSetsStore()
const accountsStore = useAccountsStore()
const entriesStore = useEntriesStore()

/** 金额呈现：固定两位小数，与后端的 decimal(...,2) 对齐。 */
const amountFormatter = new Intl.NumberFormat('zh-CN', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/** 后端回传的是带 `Z` 的 UTC 时间，交给 `Intl` 按浏览器本地时区呈现。 */
const dateTimeFormatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'short',
})

/** `YYYY-MM-DD`，`<input type="date">` 的原生取值格式。 */
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/

/**
 * 金额字段缺失时单元格显示的标记文案。
 *
 * 取值刻意**不像一个金额、也不像一个正常占位符**（页内其余占位一律是 `—`）：
 * 它要让人一眼看出「这不是数据，是异常」，而不是被当成空值的另一种写法。
 */
const AMOUNT_UNAVAILABLE = '金额异常'

const errorMessage = ref('')
const notice = ref('')

/** 时间区间的草稿（本地日期文本，`YYYY-MM-DD`）。 */
const draftFrom = ref('')
const draftTo = ref('')

/** 账户多选的草稿：勾选中的账户主键。 */
const selectedIds = ref<number[]>([])

/** 已应用的条件；`null` 表示本次进入页面后还没查过。 */
interface AppliedFilter {
  /** 呈现用的本地日期，仅用于空态与提示文案。 */
  fromDate: string
  toDate: string
  /** 发往后端的 UTC 闭区间端点。 */
  from: string
  to: string
  /** 本次筛选的账户主键。 */
  accountIds: number[]
}

const applied = ref<AppliedFilter | null>(null)

/** 当前页码，从 1 开始。 */
const pageIndex = ref(1)

/** 是否已选定账套；未选定时页面只提示，不展示筛选区与表格。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/** 账户多选的候选：当前账套内我可见的全部账户（**含已停用**，已由后端排除账本账户）。 */
const accountOptions = computed(() => accountsStore.accounts)

const items = computed(() => entriesStore.page?.items ?? [])
const totalCount = computed(() => entriesStore.page?.total ?? 0)
const totalPages = computed(() => Math.max(1, Math.ceil(totalCount.value / ENTRY_PAGE_SIZE)))

/** 本页是否存在金额字段缺失的行；只在真出现异常时才给说明，正常页面上不加噪音。 */
const hasAmountAnomaly = computed(() => items.value.some((entry) => !hasSignedAmount(entry)))

/**
 * 本页小计：收入合计 / 支出合计 / 净额合计。
 *
 * 口径是**当页可见数据之和**——期初余额行按正负号入列后一并计入（它同样带 `signedAmount`），
 * 故小计恒等于三列本页数据相加，用户核对时不会对不上。支出合计累加的是负数，
 * 呈现时由 {@link formatSigned} 补回 `-` 号。
 *
 * 金额字段缺失时 `undefined` 会把合计污染成 `NaN`，小计于是显示 {@link AMOUNT_UNAVAILABLE}——
 * 这是**刻意保留**的：只要有一行金额不明，这份小计就确实不可信，不该给出一个看着正常的数字。
 */
const pageTotals = computed(() => {
  let income = 0
  let expense = 0

  for (const entry of items.value) {
    if (entry.signedAmount < 0) {
      expense += entry.signedAmount
    } else {
      income += entry.signedAmount
    }
  }

  return { income, expense, net: income + expense }
})

/** 空态文案：带上本次查询的条件概要，让「没查到」与「查错了条件」当场可分辨。 */
const emptyText = computed(() => {
  const filter = applied.value
  if (filter === null) {
    return '请选择时间区间与账户后点击【查询】。'
  }

  return `${filter.fromDate} ~ ${filter.toDate} 内所选 ${filter.accountIds.length} 个账户没有交易明细。`
})

/** 补零到两位。 */
function pad(value: number): string {
  return String(value).padStart(2, '0')
}

/**
 * 本地时区的「今天」。
 *
 * **不得**用 `toISOString().slice(0, 10)`：那是 UTC 日期，东八区在本地 08:00 之前会整体前移一天。
 */
function todayLocal(): string {
  const now = new Date()
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

/** 本地时区「本月 1 日」。 */
function monthStartLocal(): string {
  const now = new Date()
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-01`
}

/**
 * 把本地日期文本转成 UTC 的 ISO 8601 时刻。
 *
 * 按 `YYYY-MM-DD` 拆三段后用 `new Date(y, m - 1, d, ...)` 构造——**这是本地时间**，再由
 * `toISOString()` 折成 UTC。直接 `new Date('2026-09-01')` 会按 UTC 解释该字符串，东八区下
 * 起点会落到本地 08:00，区间整体错位。
 *
 * @param date 本地日期文本。
 * @param endOfDay 是否取当日 23:59:59.999；否则取 00:00:00.000。
 * @returns UTC 时刻的 ISO 文本；日期不合法时返回 `null`。
 */
function toUtcIso(date: string, endOfDay: boolean): string | null {
  const matched = DATE_PATTERN.exec(date)
  if (matched === null) {
    return null
  }

  const year = Number(matched[1])
  const month = Number(matched[2])
  const day = Number(matched[3])
  if (!Number.isFinite(year) || !Number.isFinite(month) || !Number.isFinite(day)) {
    return null
  }

  const at = endOfDay
    ? new Date(year, month - 1, day, 23, 59, 59, 999)
    : new Date(year, month - 1, day, 0, 0, 0, 0)

  return Number.isNaN(at.getTime()) ? null : at.toISOString()
}

/** 格式化金额；无法解析时原样回显。 */
function formatAmount(value: number): string {
  return Number.isFinite(value) ? amountFormatter.format(value) : String(value)
}

/**
 * 本行的 `signedAmount` 是否是一个可用的数。
 *
 * 后端未返回该字段时它是 `undefined`（例如后端进程未按最新代码重建）。
 * 这个判定**必须挡在分列之前**：`undefined > 0` 与 `undefined < 0` 同为 `false`，
 * 一漏过去收入、支出两列就会**双双留空**——那看起来像「这些行真的没有收支」，
 * 而不是「金额没取到」。
 *
 * @param entry 一条明细。
 * @returns 字段存在且为有限数时返回 `true`。
 */
function hasSignedAmount(entry: Entry): boolean {
  return Number.isFinite(entry.signedAmount)
}

/**
 * 格式化**带符号**金额：正数补 `+`、负数用 `-`，两者共用同一套两位小数口径。
 *
 * 符号手工拼接而非交给 `Intl`（它对正数不出 `+`），且用的是 ASCII 连字符——
 * 让「收入 +100.00 / 支出 -50.00」在等宽字体下纵向对齐。
 * 符号是金额本身的一部分（本页不再有单独的「方向」列），故 `+` 不能省。
 *
 * @param value 带符号金额（后端 `signedAmount`）。
 * @returns 带正负号的金额文本；非有限数返回 {@link AMOUNT_UNAVAILABLE} 而非 `String(value)`
 * ——后者会把 `undefined` / `NaN` 原样渲染进表格，看起来像数据。
 */
function formatSigned(value: number): string {
  if (!Number.isFinite(value)) {
    return AMOUNT_UNAVAILABLE
  }

  return `${value < 0 ? '-' : '+'}${formatAmount(Math.abs(value))}`
}

/** 格式化 ISO 时间；无法解析时原样回显。 */
function formatDateTime(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : dateTimeFormatter.format(parsed)
}

/**
 * 对手方呈现文案。
 *
 * `Account` 档直接给可见账户的名称（该档的标签是空串，见 `COUNTERPARTY_KIND_LABELS`）；
 * 其余档位一律给占位文案：`Ledger` 显示「期初」，`Hidden` 与 `None` 显示「—」。
 * 后端不会为不可见对手方回传主键与名称，故这里也不存在「回退到 Id」的分支。
 */
function counterpartyText(entry: Entry): string {
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
function categoryText(entry: Entry): string {
  return entry.categoryName ?? '—'
}

/**
 * 收入列文本：只有正数行有值，其余行留空。
 *
 * 「留空」是这一列的正常语义（这行不是收入），故仅在**金额字段不可用**时才给标记——
 * 那种情况下「留空」与「没有收入」无法区分，只能显式说明。
 */
function incomeText(entry: Entry): string {
  if (!hasSignedAmount(entry)) {
    return AMOUNT_UNAVAILABLE
  }

  return entry.signedAmount > 0 ? formatSigned(entry.signedAmount) : ''
}

/** 支出列文本：只有负数行有值，其余行留空；字段不可用时同 {@link incomeText} 给标记。 */
function expenseText(entry: Entry): string {
  if (!hasSignedAmount(entry)) {
    return AMOUNT_UNAVAILABLE
  }

  return entry.signedAmount < 0 ? formatSigned(entry.signedAmount) : ''
}

/**
 * 窄屏卡片头部的收支标签：该行算收入还是支出。
 *
 * 它**与宽屏的两列同源**——判据是 `signedAmount` 的正负，即 {@link incomeText} / {@link expenseText}
 * 分列所依据的同一个符号，故不构成第二个语义源。窄屏没有列头，「金额落在哪一列」这一层信息随之丢失，
 * 而这个标签把它补回来；金额本身仍带 `+` / `-` 号，是色彩之外的第二条通道。
 *
 * **不得改写成 `entry.direction` 分支**：那等于把「借/贷」放回页面（本页刻意弱化复式记账），
 * 且会与「按符号分列」的既有口径分叉成两个真源。
 *
 * @param entry 一条明细。
 * @returns `收入` / `支出`；零值与金额字段不可用时返回**空串**——零值行在宽屏两列下同样两列皆空，
 * 口径一致；而字段不可用时既不知道是收入、也不知道是支出，说成「收入」就是把缺失值标成正常数据
 * （同 #44 的口径：异常必须显式呈现，由 {@link formatSigned} 给出标记）。
 */
function sideText(entry: Entry): string {
  if (!hasSignedAmount(entry)) {
    return ''
  }

  if (entry.signedAmount > 0) {
    return '收入'
  }

  return entry.signedAmount < 0 ? '支出' : ''
}

/**
 * 金额单元格的语义色类。
 *
 * @param value 带符号金额。
 * @param side 该单元格归属的列：收入列只在正数时着绿，支出列只在负数时着红，净额列两向都着色
 * （净额是「这一行的增减合计」，正负两种结果都是它的正常取值）。
 * @returns 语义色类名；**非有限数返回告警类**——不能让它落进 `expense` 分支
 * （`NaN < 0` 为 `false`，会被判成收入而着绿，等于把一个缺失值标成正常收入）。
 */
function signedCellClass(value: number, side: 'income' | 'expense' | 'net'): string {
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

/** 拉取账户筛选候选；含已停用账户。 */
async function loadAccounts(): Promise<boolean> {
  try {
    await accountsStore.list(true)
    return true
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载账户失败'
    return false
  }
}

/** 按已应用条件取当前页。 */
async function load(): Promise<void> {
  const filter = applied.value
  if (filter === null || !hasAccountSet.value) {
    return
  }

  errorMessage.value = ''
  try {
    const result = await entriesStore.query({
      from: filter.from,
      to: filter.to,
      accountIds: filter.accountIds,
      page: pageIndex.value,
      pageSize: ENTRY_PAGE_SIZE,
    })
    notice.value = `共 ${result.total} 条明细`
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载账目明细失败'
  }
}

/**
 * 执行查询：把草稿校验后落成已应用条件，并从第 1 页取数。
 *
 * 三处校验都在前端拦下、**不发请求**：条件明显不完整时发出去只会拿到一个后端错误，
 * 而用户真正需要知道的是「哪个条件没填」。
 */
async function search(): Promise<void> {
  if (!hasAccountSet.value) {
    return
  }

  errorMessage.value = ''
  notice.value = ''

  if (draftFrom.value.length === 0 || draftTo.value.length === 0) {
    errorMessage.value = '请选择完整的时间区间'
    return
  }

  // 同格式日期可直接按字典序比较，无需先转 Date
  if (draftFrom.value > draftTo.value) {
    errorMessage.value = '结束日期不能早于起始日期'
    return
  }

  if (selectedIds.value.length === 0) {
    errorMessage.value = '请至少选择一个账户'
    return
  }

  const from = toUtcIso(draftFrom.value, false)
  const to = toUtcIso(draftTo.value, true)
  if (from === null || to === null) {
    errorMessage.value = '请选择完整的时间区间'
    return
  }

  applied.value = {
    fromDate: draftFrom.value,
    toDate: draftTo.value,
    from,
    to,
    accountIds: [...selectedIds.value],
  }
  pageIndex.value = 1

  await load()
}

/** 翻页：复用已应用条件，只换页码。 */
async function go(page: number): Promise<void> {
  if (page < 1 || page > totalPages.value || entriesStore.loading) {
    return
  }

  pageIndex.value = page
  await load()
}

/** 账户全选。 */
function selectAllAccounts(): void {
  selectedIds.value = accountOptions.value.map((account) => account.id)
}

/** 账户全不选。 */
function clearAccounts(): void {
  selectedIds.value = []
}

/** 复位为「本月 + 全选可见账户」并立即查询；这是进入页面与切换账套后的默认视图。 */
async function resetToDefault(): Promise<void> {
  draftFrom.value = monthStartLocal()
  draftTo.value = todayLocal()
  selectedIds.value = []
  applied.value = null
  pageIndex.value = 1
  errorMessage.value = ''
  notice.value = ''
  entriesStore.clear()

  if (!(await loadAccounts())) {
    return
  }

  selectAllAccounts()
  await search()
}

// 账套切换后必须重来一遍：明细按账套隔离，沿用旧结果会显示上一账套的数据。
// 未选择账套时清空，避免退出登录后仍残留可见数据。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    if (currentId === null) {
      accountsStore.clear()
      entriesStore.clear()
      applied.value = null
      selectedIds.value = []
      errorMessage.value = ''
      notice.value = ''
      return
    }

    await resetToDefault()
  },
)

onMounted(() => {
  if (hasAccountSet.value) {
    void resetToDefault()
  }
})
</script>

<template>
  <main class="entries">
    <header class="head">
      <div>
        <h1 class="title">账目明细</h1>
        <p class="subtitle">
          在当前账套内按时间区间与账户（可多选）查询交易明细，按业务发生时间正序排列。
          钱进来记在【收入】列（绿色，带 + 号），钱出去记在【支出】列（红色，带 − 号），
          【净额】列是这一行的增减合计；表格底部给出当页小计。
          【分类】列是这笔交易的分类（记账时可选，未分类显示 —）。
          一笔交易涉及两个所选账户时会呈现两行。时间取业务发生时间，可补记往日收支。
        </p>
      </div>
    </header>

    <!-- 未选定账套：明细必然落在某个账套内，此时不渲染筛选区与表格 -->
    <p v-if="!hasAccountSet" class="hint">
      当前未选择账套，请先点击右上角的【切换】选择账套后再查询账目明细。
    </p>

    <template v-else>
      <section class="filters">
        <div class="field">
          <label class="label" for="entry-from">起始日期</label>
          <input id="entry-from" v-model="draftFrom" type="date" :max="draftTo || undefined" />
        </div>

        <div class="field">
          <label class="label" for="entry-to">结束日期</label>
          <input id="entry-to" v-model="draftTo" type="date" :min="draftFrom || undefined" />
        </div>

        <div class="filter-actions">
          <!-- 查询是页面的主操作：条件改动后需用户点它才生效，避免边改边查 -->
          <button type="button" class="submit" :disabled="entriesStore.loading" @click="search">
            {{ entriesStore.loading ? '查询中…' : '查询' }}
          </button>
          <button
            type="button"
            class="ghost"
            :disabled="entriesStore.loading"
            @click="resetToDefault"
          >
            重置
          </button>
        </div>
      </section>

      <section class="picker">
        <div class="picker-head">
          <span class="label">账户（可多选）</span>
          <span class="picker-count">
            已选 {{ selectedIds.length }} / {{ accountOptions.length }}
          </span>
          <div class="picker-actions">
            <button
              type="button"
              class="ghost"
              :disabled="accountOptions.length === 0"
              @click="selectAllAccounts"
            >
              全选
            </button>
            <button
              type="button"
              class="ghost"
              :disabled="selectedIds.length === 0"
              @click="clearAccounts"
            >
              全不选
            </button>
          </div>
        </div>

        <div class="picker-list">
          <label v-for="account in accountOptions" :key="account.id" class="picker-item">
            <input v-model="selectedIds" type="checkbox" :value="account.id" />
            <span class="picker-name">{{ account.name }}</span>
            <!-- 已停用账户照常可选：历史明细仍挂在它上面，且其明细只会出现在它被选中时 -->
            <span v-if="!account.isActive" class="badge badge-inactive">已停用</span>
          </label>
          <p v-if="accountOptions.length === 0" class="picker-empty">当前账套内没有可选的账户。</p>
        </div>

        <p class="picker-hint">
          日期区间为闭区间（含起止当天），默认本月 1
          日至今天。账本账户为系统内部账户，不在筛选列表中。
        </p>
      </section>

      <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
      <p v-if="notice" class="notice">{{ notice }}</p>

      <!-- 金额字段缺失时必须说明标记的含义：三列表的空白本身是语义，
           缺字段却留空会让异常看起来像「这些行真的没有收支」 -->
      <p v-if="hasAmountAnomaly" class="amount-warning">
        收入/支出/净额列显示【{{ AMOUNT_UNAVAILABLE }}】，表示后端未返回金额字段 signedAmount
        ——通常是后端服务未按最新代码重新构建启动。此时既无法判定这些行是收入还是支出，
        【本页小计】也随之不可信。请重启后端服务后重新查询。
      </p>

      <table class="table">
        <thead>
          <tr>
            <th>序号</th>
            <th>发生时间</th>
            <th>账户</th>
            <th class="amount">收入</th>
            <th class="amount">支出</th>
            <th class="amount">净额</th>
            <th>对手方</th>
            <th>摘要</th>
            <th>分类</th>
            <th>备注</th>
          </tr>
        </thead>
        <tbody>
          <!-- 序号是页内行号：与明细主键无关，翻页后从 1 重新开始 -->
          <tr v-for="(entry, index) in items" :key="entry.id">
            <td class="row-index">{{ index + 1 }}</td>
            <td class="occurred">{{ formatDateTime(entry.occurredAt) }}</td>
            <td class="name">{{ entry.accountName }}</td>
            <!-- 收入/支出各占一列，同一行只有一列有值：金额的增减不再靠「借/贷」标签表达 -->
            <td class="amount" :class="signedCellClass(entry.signedAmount, 'income')">
              {{ incomeText(entry) }}
            </td>
            <td class="amount" :class="signedCellClass(entry.signedAmount, 'expense')">
              {{ expenseText(entry) }}
            </td>
            <td class="amount" :class="signedCellClass(entry.signedAmount, 'net')">
              {{ formatSigned(entry.signedAmount) }}
            </td>
            <td class="counterparty">{{ counterpartyText(entry) }}</td>
            <td>
              <span class="summary">{{ entry.summary }}</span>
              <!-- 交易类型是明细的背景信息，弱化呈现，不占一列 -->
              <span class="type">{{ transactionTypeLabel(entry.transactionType) }}</span>
            </td>
            <!-- 分类是交易级属性：一笔交易的两条明细会显示同一个分类，这不是重复 -->
            <td class="category">{{ categoryText(entry) }}</td>
            <td class="remark">{{ entry.remark || '—' }}</td>
          </tr>
          <tr v-if="items.length === 0">
            <td colspan="10" class="empty">{{ emptyText }}</td>
          </tr>
        </tbody>
        <!-- 小计只统计**当页**：跨页合计会让「本页小计」这个标题名不副实，
             全区间合计应由账户页的余额或另设的汇总能力承担 -->
        <tfoot v-if="items.length > 0">
          <tr>
            <td colspan="3" class="subtotal-label">本页小计</td>
            <td class="amount" :class="signedCellClass(pageTotals.income, 'income')">
              {{ formatSigned(pageTotals.income) }}
            </td>
            <td class="amount" :class="signedCellClass(pageTotals.expense, 'expense')">
              {{ formatSigned(pageTotals.expense) }}
            </td>
            <td class="amount" :class="signedCellClass(pageTotals.net, 'net')">
              {{ formatSigned(pageTotals.net) }}
            </td>
            <td colspan="4"></td>
          </tr>
        </tfoot>
      </table>

      <!--
        窄屏（<1024px）的卡片流：与上面的表格是同一份 `items` 的双呈现，默认 display:none，
        由文末的 1023px 媒体查询启用（同时把表格整块隐藏）。两处必须同步改：
        卡片不呈现序号，且金额只给一格（理由见文件头注释）。
      -->
      <ul class="cards">
        <li v-for="entry in items" :key="entry.id" class="card">
          <div class="card-head">
            <span class="card-side">
              <!-- 收支标签由金额正负派生，与宽屏两列同源；零值/字段缺失时为空串 -->
              <span class="card-side-text">{{ sideText(entry) }}</span>
              <!-- 交易类型与宽屏一样是背景信息：一笔转账正是靠它区别于真实收支 -->
              <span class="card-type">{{ transactionTypeLabel(entry.transactionType) }}</span>
            </span>
            <!-- 金额是卡片的视觉主位：带符号 + 按符号着色（净额口径），字段缺失走金额异常告警 -->
            <span class="card-amount" :class="signedCellClass(entry.signedAmount, 'net')">
              {{ formatSigned(entry.signedAmount) }}
            </span>
          </div>

          <!-- 账户 → 对手方：「钱从哪来、到哪去」的对照，宽屏下它们是两列 -->
          <p class="card-account">
            <span class="card-account-name">{{ entry.accountName }}</span>
            <span class="card-arrow" aria-hidden="true">→</span>
            <span class="card-counterparty">{{ counterpartyText(entry) }}</span>
          </p>

          <p class="card-summary">{{ entry.summary }}</p>

          <p class="card-meta">
            <span class="card-time">{{ formatDateTime(entry.occurredAt) }}</span>
            <span class="card-category">分类：{{ categoryText(entry) }}</span>
            <span class="card-remark">备注：{{ entry.remark || '—' }}</span>
          </p>
        </li>

        <!-- 空态与表格空态行共用同一 emptyText，两处文案不得各自表述 -->
        <li v-if="items.length === 0" class="card card-empty">{{ emptyText }}</li>
      </ul>

      <!--
        窄屏的本页小计：与宽屏 <tfoot> 同源同值（同一 pageTotals / formatSigned / signedCellClass），
        同样只在有数据时渲染。三项缺一不可——收入/支出/净额只有在**合计**时才互不相等。
      -->
      <section v-if="items.length > 0" class="totals">
        <p class="totals-title">本页小计</p>
        <div class="totals-grid">
          <div class="total">
            <span class="total-label">收入</span>
            <span class="total-value" :class="signedCellClass(pageTotals.income, 'income')">
              {{ formatSigned(pageTotals.income) }}
            </span>
          </div>
          <div class="total">
            <span class="total-label">支出</span>
            <span class="total-value" :class="signedCellClass(pageTotals.expense, 'expense')">
              {{ formatSigned(pageTotals.expense) }}
            </span>
          </div>
          <div class="total">
            <span class="total-label">净额</span>
            <span class="total-value" :class="signedCellClass(pageTotals.net, 'net')">
              {{ formatSigned(pageTotals.net) }}
            </span>
          </div>
        </div>
      </section>

      <div class="pager">
        <button
          type="button"
          class="ghost"
          :disabled="pageIndex <= 1 || entriesStore.loading"
          @click="go(pageIndex - 1)"
        >
          上一页
        </button>
        <span class="pager-info">
          第 {{ pageIndex }} / {{ totalPages }} 页 · 共 {{ totalCount }} 条
        </span>
        <button
          type="button"
          class="ghost"
          :disabled="pageIndex >= totalPages || entriesStore.loading"
          @click="go(pageIndex + 1)"
        >
          下一页
        </button>
      </div>
    </template>
  </main>
</template>

<style scoped>
.entries {
  /* 铺满内容区：限宽与居中的职责归 .app-main，页面自身既不限宽也不叠加外边距 */
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

.title {
  font-size: 20px;
  font-weight: 600;
  color: var(--color-heading);
}

.subtitle {
  margin-top: 0.35rem;
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.75;
}

.hint {
  padding: 1rem 1.25rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-card);
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.8;
}

.error,
.notice,
.amount-warning {
  padding: 0.5rem 0.7rem;
  border-radius: var(--radius-control);
  font-size: 13px;
  line-height: 1.7;
}

/* 金额字段缺失说明与 .error 同源：它同样是「需要立刻处理」的告知，
   只是触发方是后端而非用户操作，故不另立一套配色。 */
.error,
.amount-warning {
  border: 1px solid var(--color-danger-border);
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

/* 提示态复用主色：提示是「中性告知」，不占用收支语义色（绿/红只表达金额正负） */
.notice {
  border: 1px solid var(--color-accent);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
}

/* 筛选区：日期字段与操作按钮同处一行，窄屏下自动折行 */
.filters {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 0.75rem;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  min-width: 9rem;
}

.label {
  font-size: 12.5px;
  opacity: 0.75;
}

.field input {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  box-shadow: var(--shadow-control);
}

.field input:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

/* 按钮与日期输入框底部对齐：输入框下方无多余留白，故只需与字段基线对齐 */
.filter-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

/* 账户多选面板：受边框约束的独立区域，与下方表格区分开 */
.picker {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.75rem 0.9rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

.picker-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.6rem;
}

.picker-count {
  font-size: 12.5px;
  opacity: 0.7;
}

/* 全选/全不选靠右固定，与左侧说明拉开 */
.picker-actions {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin-left: auto;
}

/* 账户多时只在本区域内滚动，不把页面撑长 */
.picker-list {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem 1rem;
  max-height: 10rem;
  overflow-y: auto;
  padding: 0.5rem 0.6rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  background: var(--color-background-mute);
}

.picker-item {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 13px;
  cursor: pointer;
}

.picker-name {
  white-space: nowrap;
}

.picker-empty {
  font-size: 13px;
  opacity: 0.6;
}

.picker-hint {
  font-size: 12.5px;
  line-height: 1.7;
  opacity: 0.7;
}

.table {
  width: 100%;
  border-collapse: collapse;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  overflow: hidden;
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
  font-size: 13.5px;
}

.table th,
.table td {
  padding: 0.65rem 0.75rem;
  text-align: left;
  border-bottom: 1px solid var(--color-border);
  vertical-align: middle;
}

.table thead th {
  font-size: 12.5px;
  font-weight: 600;
  opacity: 0.75;
  background: var(--color-background-mute);
}

.table tbody tr:last-child td {
  border-bottom: 0;
}

/* 列表行号：按当前页顺序连续编号，与明细主键无关 */
.row-index {
  opacity: 0.6;
  white-space: nowrap;
}

.occurred {
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.name {
  font-weight: 600;
}

/* 金额右对齐并等宽呈现，便于纵向比对；收入/支出/净额三列共用 */
.amount {
  text-align: right;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
  font-weight: 600;
}

/* 收支语义色：正数绿（收入侧）、负数红（支出侧）。
   语义色令牌见 base.css——支出红复用危险色，故全站仍只有一个红。 */
.income {
  color: var(--color-income);
}

.expense {
  color: var(--color-expense);
}

/* 金额字段缺失的单元格：复用危险色，但语义与 .expense 完全不同——
   红色在这里说的是「这个值不可信」，不是「这是一笔支出」。
   两者共用同一个色令牌是刻意的：全站只有一个红，不再引入第二种告警色。 */
.amount-unknown {
  color: var(--color-danger);
}

/* 小计行：用上边框与底色把它和明细行分开，读作「汇总」而不是「又一条明细」 */
.table tfoot td {
  padding: 0.6rem 0.75rem;
  border-top: 1px solid var(--color-border-hover);
  background: var(--color-background-mute);
  font-size: 13px;
  font-weight: 600;
}

.table tfoot tr:last-child td {
  border-bottom: 0;
}

.subtotal-label {
  font-weight: 600;
  opacity: 0.85;
}

.counterparty {
  white-space: nowrap;
}

.summary {
  margin-right: 0.4rem;
}

/* 交易类型弱化：它是明细的背景信息，不抢摘要的视觉重心 */
.type {
  font-size: 12px;
  opacity: 0.6;
  white-space: nowrap;
}

/* 分类与备注同为次要信息，弱化到同一档；未分类的「—」随之一起变淡 */
.category {
  white-space: nowrap;
  opacity: 0.75;
}

.remark {
  opacity: 0.75;
}

.badge {
  display: inline-block;
  padding: 0.15rem 0.5rem;
  border-radius: 999px;
  border: 1px solid transparent;
  font-size: 12px;
  white-space: nowrap;
}

/* 「借/贷」徽标已随方向列移除：复式记账是数据的组织方式，不是普通人读账的方式。
   .badge 与 .badge-inactive 仍被下方账户多选面板的「已停用」使用，故保留。 */
.badge-inactive {
  border-color: var(--color-danger-border);
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

.empty {
  text-align: center;
  padding: 1.5rem;
  opacity: 0.6;
}

.pager {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 0.6rem;
}

.pager-info {
  font-size: 13px;
  opacity: 0.75;
  font-variant-numeric: tabular-nums;
}

.ghost,
.submit {
  padding: 0.3rem 0.7rem;
  border-radius: var(--radius-control);
  background: none;
  font-size: 12.5px;
  font-family: inherit;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s;
}

.ghost {
  border: 1px solid var(--color-accent);
  color: var(--color-accent-strong);
}

/* 实心主色按钮：与账户页的「新增」同源 */
.submit {
  display: inline-flex;
  align-items: center;
  padding: 0.5rem 1rem;
  border: 1px solid var(--color-accent);
  background: var(--color-accent);
  color: var(--color-accent-contrast);
  font-size: 13px;
  font-weight: 600;
}

@media (hover: hover) {
  .ghost:not(:disabled):hover {
    background-color: var(--color-accent-soft);
  }

  .submit:not(:disabled):hover {
    border-color: var(--color-accent-strong);
    background: var(--color-accent-strong);
  }
}

.ghost:disabled,
.submit:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

/*
 * 窄屏卡片流与汇总条：**默认不呈现**（宽屏走表格），由文末的 1023px 媒体查询启用。
 * display: none 在这里是断点级的双呈现手段、不是死代码——理由见文件头注释。
 */
.cards,
.totals {
  display: none;
}

.cards {
  flex-direction: column;
  gap: 0.6rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

/* 卡片与表格同源：同一套边框/圆角/底色/投影令牌，只是把「行」换成「块」 */
.card {
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
  padding: 0.75rem 0.85rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

.card-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 0.6rem;
}

.card-side {
  display: flex;
  align-items: baseline;
  gap: 0.4rem;
  min-width: 0;
}

.card-side-text {
  font-size: 13px;
  font-weight: 600;
  opacity: 0.75;
}

/* 交易类型与宽屏一样弱化：它是明细的背景信息，不抢金额与摘要的视觉重心 */
.card-type {
  font-size: 12px;
  opacity: 0.6;
  white-space: nowrap;
}

/* 金额是卡片的视觉主位：字号明显大于正文，等宽便于纵向比对大小 */
.card-amount {
  flex: none;
  font-size: 16px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.card-account {
  display: flex;
  align-items: baseline;
  gap: 0.35rem;
  font-size: 13.5px;
  font-weight: 600;
}

/* 账户名占弹性空间：空间不足时截断，不去挤右侧的对手方 */
.card-account-name {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-arrow,
.card-counterparty {
  flex: none;
}

/* 摘要允许换行、不截断：它是这一行唯一的自由文本，截断等于丢信息 */
.card-summary {
  font-size: 13.5px;
  overflow-wrap: anywhere;
}

/* 时间 / 分类 / 备注同属「每行都要读得到、又都不抢金额视线」的次要信息，排一行 */
.card-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 0.2rem 0.6rem;
  font-size: 12.5px;
  line-height: 1.6;
  opacity: 0.75;
}

.card-time {
  font-variant-numeric: tabular-nums;
}

.card-empty {
  align-items: center;
  padding: 1.5rem;
  font-size: 13px;
  text-align: center;
  opacity: 0.6;
}

/* 汇总条：底色 + 上边框把它读作「汇总」而不是「又一张卡片」，与表格 <tfoot> 同源 */
.totals {
  padding: 0.7rem 0.85rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-mute);
  box-shadow: var(--shadow-card);
}

.totals-title {
  font-size: 12.5px;
  font-weight: 600;
  opacity: 0.85;
}

/* 三项等宽平分：窄屏下并排比上下堆叠省一半高度，数字小也能对齐比对 */
.totals-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 0.5rem;
  margin-top: 0.45rem;
}

.total {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  min-width: 0;
}

.total-label {
  font-size: 12px;
  opacity: 0.7;
}

.total-value {
  overflow: hidden;
  font-size: 13px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/*
 * 窄屏：明细改卡片流，表格整块让位。
 *
 * 断点值与 `App.vue` 完全一致（1023px）且**必须**一致：全站只有这一个断点，页面级适配另起一套
 * 会让「侧栏收成抽屉」与「明细换呈现」在中间地带错位。侧栏抽屉的开合、当前页「账目明细」的自动高亮、
 * 跳转后自动收起，一概由 `App.vue` 独担，**本页不得复制任何相关逻辑**，否则就是第二个真源。
 *
 * 仅切换呈现：筛选区、账户多选面板、分页器与全部金额口径在宽窄两档下完全共用。
 */
@media (max-width: 1023px) {
  .table {
    display: none;
  }

  .cards {
    display: flex;
  }

  .totals {
    display: block;
  }
}
</style>
