<script setup lang="ts">
/**
 * /reports 页面：月报表。
 *
 * 在账目明细的实时口径上再给一层**按任意月份区间**的读法：选好区间、账户与标签后，
 * 一次请求回答四件事——收入 / 支出各按分类怎么构成（两张饼图）、按标签怎么构成（两张饼图）、
 * 每个月的收入与支出走势（双折线）、每月一行的收支与净额（表格 + 合计）。
 *
 * **与日报表同一套能力，只把时间粒度从「日」换成「月」**：日报表看「这段时间的每一天」，
 * 月报表看「这几年里的每个月」——跨年对比、按账户 / 标签收窄，都是明细页分页与单月视图
 * 表达不了的读法。两者的口径逐条相同（默认币种、公共 + 本人个人、资金 / 负债账户、
 * 只计收入与支出、转账与期初不计），差别只在区间与粒度。
 *
 * **四块数据来自同一个请求**（`GET /api/entries/monthly-report`）：后端一次取回候选明细，
 * 在内存里同时折出按月、按分类与按标签，故折线、饼图与表格的口径天然一致，
 * 不会出现「折线有、饼图没有」。页面因此只维护一个请求状态，不逐块拼装，
 * 也就没有「一块失败、另一块还在显示上一份数据」的画面。
 *
 * **补零只发生在呈现层**：后端只返回「真的发生过收支」的月份，本页按所选区间逐月补齐——
 * 月报表的数据源是实时的，空白月就是真实的 0（与首页结算图表的「缺日不补零」刻意不同：
 * 那里的空白表示「还没结算」，补 0 会把「没算」画成「就是 0」）。本月尚未结束时，
 * 本月这一点就是截至今天的发生额，不按整月折算、也不预测。
 * 表格里没有发生额的月份收入 / 支出两列显示「—」，净额仍按符号给出（零就是 `+0.00`）。
 *
 * **标签是多值维度**：一笔交易挂多个标签时，其金额在**每个标签下各计一次**（后端不摊分），
 * 故各标签金额之和可能大于区间收支合计；这一点在标签契约里逐字写明，页面不试图「修正」它。
 * 分类则是交易头上的**一个**分类，不存在重复计入。没有标签的交易归入「未打标签」，
 * 与「未分类」一样是合法的归集结果，不是数据缺失。
 *
 * **筛选是显式的**：月份区间、账户勾选、标签勾选都先落在草稿上，点【查询】才生效——
 * 与日报表、账目明细页同一取舍（本页有三维条件，边改边查会让每一次勾选都触发一次全量聚合）。
 * 页面不做「至少勾一个账户」之外的其他校验；区间需完整、起止不倒置、最多 36 个月，
 * 后端会再校验一遍，前端先拦是为了不把一个必然 400 的请求发出去。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import CategoryPieChart from '@/components/CategoryPieChart.vue'
import TrendChart from '@/components/TrendChart.vue'
import { UNTAGGED_LABEL } from '@/components/categoryPie'
import { formatAmount, formatSigned, formatUnsigned, signedCellClass } from '@/components/entryFormat'
import type { TrendSeriesSpec } from '@/components/trendChart'
import { useAccountSetsStore } from '@/stores/accountSets'
import { MONEY_ACCOUNT_TYPES, useAccountsStore } from '@/stores/accounts'
import type { EntryCategoryTotal } from '@/stores/categoryTotals'
import { useCurrenciesStore } from '@/stores/currencies'
// 标签合计的形状直接取日报表那一份：月报表与日报表的标签维度是同一个契约（见 `stores/monthlyReport.ts`）
import type { ReportTagTotal } from '@/stores/dailyReport'
import { useMonthlyReportStore } from '@/stores/monthlyReport'
import { useTagsStore } from '@/stores/tags'

const accountSets = useAccountSetsStore()
const accountsStore = useAccountsStore()
const currenciesStore = useCurrenciesStore()
const tagsStore = useTagsStore()
const monthlyReport = useMonthlyReportStore()

/** 后端约定的月份文本格式。 */
const MONTH_PATTERN = /^(\d{4})-(\d{2})$/

/**
 * 区间上限（月）。
 *
 * 与后端 `MAX_REPORT_MONTHS` 保持一致：后端卡 36 个月是为了不让一次查询扫过无限长的历史；
 * 前端先拦一遍只是省掉一次必然返回 400 的往返，**真正生效的仍是后端那道校验**。
 */
const MAX_REPORT_MONTHS = 36

/** 默认区间长度（月，含当月）。 */
const DEFAULT_REPORT_MONTHS = 12

/** 是否已选定账套；未选定时页面只提示、不请求（月报表按账套隔离）。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/** 补零到两位。 */
function pad(value: number): string {
  return String(value).padStart(2, '0')
}

/**
 * 浏览器本地的当前月份（`yyyy-MM`）。
 *
 * **不得**用 `toISOString().slice(0, 7)`：那是 UTC 月份，东八区在每月 1 日 08:00 之前会整体落到上个月。
 */
function currentMonthLocal(): string {
  const now = new Date()
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}`
}

/**
 * 浏览器本地「当前月往前推 `offset` 个月」的月份（`yyyy-MM`）。
 *
 * 逐月构造 `new Date(year, month - offset, 1)` 而不是对同一个 Date 反复 `setMonth`：
 * 前者每次都从原始年月出发，跨年与「31 号减一个月」都由平台处理，不会把日期漂移累积进结果。
 */
function monthsAgoLocal(offset: number): string {
  const now = new Date()
  const at = new Date(now.getFullYear(), now.getMonth() - offset, 1)
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}`
}

/**
 * 把 `yyyy-MM` 解析成「从公元 0 年起的月序号」；格式或值非法时返回 `null`。
 *
 * 月序号 = 年 × 12 + (月 − 1)，于是「两个月份隔了几个月」只是两个整数的差，
 * 不必引入任何日期算术（也就不必担心时区与夏令时）。
 */
function monthIndex(value: string): number | null {
  const matched = MONTH_PATTERN.exec(value)
  if (matched === null) {
    return null
  }

  const year = Number(matched[1])
  const month = Number(matched[2])
  if (!Number.isInteger(year) || month < 1 || month > 12) {
    return null
  }

  return year * 12 + (month - 1)
}

/** 月序号还原成 `yyyy-MM`。 */
function monthText(index: number): string {
  const year = Math.floor(index / 12)
  const month = (index % 12) + 1
  return `${year}-${pad(month)}`
}

/**
 * 两个月份之间的月数（闭区间，含起止两端）。
 *
 * @returns 月数；任一月份非法或结束早于开始时为 0。
 */
function monthCountBetween(from: string, to: string): number {
  const start = monthIndex(from)
  const end = monthIndex(to)
  if (start === null || end === null) {
    return 0
  }

  return end - start + 1
}

/**
 * 枚举区间内的每一个月（`yyyy-MM`，升序）。
 *
 * 月数超过上限或区间非法时返回空数组——调用方据此不再画图。
 */
function enumerateMonths(from: string, to: string): string[] {
  const count = monthCountBetween(from, to)
  const start = monthIndex(from)
  if (start === null || count <= 0 || count > MAX_REPORT_MONTHS) {
    return []
  }

  const months: string[] = []
  for (let offset = 0; offset < count; offset += 1) {
    months.push(monthText(start + offset))
  }

  return months
}

/** 起始月份草稿；默认当前月往前推 11 个月（含当月共 12 个月）。 */
const fromMonth = ref(monthsAgoLocal(DEFAULT_REPORT_MONTHS - 1))
/** 结束月份草稿；默认当前月（浏览器本地）。 */
const toMonth = ref(currentMonthLocal())
/** 已勾选的账户主键；候选就绪后默认全选。 */
const selectedAccountIds = ref<number[]>([])
/** 已勾选的标签主键；空数组即「不限标签」。 */
const selectedTagIds = ref<number[]>([])

/** 前端校验文案；为空串表示条件可用。 */
const filterError = ref('')
/** 请求或候选加载的失败文案；为空串表示没有失败。 */
const errorMessage = ref('')

/** 最近一次查询结果；`null` 表示还没查过。 */
const report = computed(() => monthlyReport.report)

/** 当前查看的区间文案；未查过时为占位文案。 */
const periodLabel = computed(() => {
  const data = report.value
  if (data === null) {
    return '所选区间'
  }

  return data.from === data.to ? data.from : `${data.from} 至 ${data.to}`
})

/** 金额所属币种；未查过或没有可用币种时为 `null`。 */
const currencyCode = computed(() => report.value?.currencyCode ?? null)

/** 区间内是否至少有一笔收入或支出。 */
const hasData = computed(() => (report.value?.months.length ?? 0) > 0)

/**
 * 可选的账户候选：当前账套内**系统默认币种**下的资金 / 负债账户，含已停用。
 *
 * 按默认币种过滤的理由与后端逐条一致：报表口径固定为默认币种，其他币种账户上的收支
 * 根本不进这次聚合。与账目明细页不同——那里还能按账户选币种外账户看各自明细，
 * 这里若列出来，勾选后只会得到一片空白，看起来像「这个账户没有收支」。
 *
 * 含已停用账户的理由同明细页：停用是「不再用于新记账」，不是「历史上没有发生过」。
 */
const moneyAccountOptions = computed(() => {
  const code = currenciesStore.defaultCode
  if (code === null) {
    return []
  }

  return accountsStore.accounts.filter(
    (account) => MONEY_ACCOUNT_TYPES.includes(account.type) && account.currencyCode === code,
  )
})

/** 标签候选：当前账套内的标签，含已停用（理由同上）。 */
const tagOptions = computed(() => tagsStore.tags)

/**
 * 月份区间的校验文案。
 *
 * 三种情形分开说：没选完整、起止倒置、超过上限——同一个「条件不合法」下面藏着三种
 * 不同的改法，笼统写一句会让人不知道下一步点哪里。
 */
const rangeError = computed(() => {
  if (fromMonth.value === '' || toMonth.value === '') {
    return '请选择完整的起始与结束月份。'
  }

  const count = monthCountBetween(fromMonth.value, toMonth.value)
  if (count <= 0) {
    return '结束月份不能早于起始月份。'
  }

  if (count > MAX_REPORT_MONTHS) {
    return `月份区间最多 ${MAX_REPORT_MONTHS} 个月，请缩小范围。`
  }

  return ''
})

/** 按月表格的一行：区间内的某个月（没有发生额的月份已补 0）。 */
interface MonthlyRow {
  /** 月份（`yyyy-MM`）。 */
  month: string
  /** 当月收入合计，恒为非负。 */
  income: number
  /** 当月支出合计，恒为非负。 */
  expense: number
  /** 净额 = 收入 − 支出（带符号）。 */
  net: number
}

/**
 * 按月表格的行：区间内**每个月**一行，没有发生额的月份补 0。
 *
 * 补零的理由见文件头；`byMonth` 只用来取有发生额的那个月，其余月份落到 `?? 0` 分支。
 * 净额在这里现算而不是取后端的 `netTotal`：表格的「合计」行也要用收入与支出两个合计相减，
 * 两处用同一个算式，就不会出现「逐行净额之和」与「合计净额」对不上的口子。
 */
const monthlyRows = computed<MonthlyRow[]>(() => {
  const data = report.value
  if (data === null) {
    return []
  }

  const byMonth = new Map(data.months.map((point) => [point.month, point]))

  return enumerateMonths(data.from, data.to).map((month) => {
    const point = byMonth.get(month)
    const income = point?.incomeTotal ?? 0
    const expense = point?.expenseTotal ?? 0

    return { month, income, expense, net: income - expense }
  })
})

/**
 * 区间合计：由按月数据相加。
 *
 * 净额用「收入合计 − 支出合计」而不是逐月累加净额：两者数学上相等，
 * 但前者少一层可以把符号弄反的机会（与日报表、首页同一算法）。
 */
const totals = computed(() => {
  let income = 0
  let expense = 0
  for (const row of monthlyRows.value) {
    income += row.income
    expense += row.expense
  }

  return { income, expense, net: income - expense }
})

/** 折线图的呈现数据；没有可画的数据时为 `null`。 */
interface ChartView {
  xLabels: string[]
  series: TrendSeriesSpec[]
  description: string
}

/**
 * 按月折线：横轴是区间内的每一个月（已补零）。
 *
 * 横轴文案直接取 `yyyy-MM`：跨年对比时年份本身就是要读的信息，而 36 个刻度由
 * ECharts 自动抽稀，不会互相压叠。读屏描述把三个合计念全——`role="img"` 的图需要一个概述，
 * 逐月数值由下方表格承担。
 */
const chart = computed<ChartView | null>(() => {
  const rows = monthlyRows.value
  if (rows.length === 0) {
    return null
  }

  const code = currencyCode.value
  const suffix = code === null ? '' : `，币种 ${code}`

  return {
    xLabels: rows.map((row) => row.month),
    series: [
      { key: 'income', label: '收入', colorToken: '--color-income', values: rows.map((row) => row.income) },
      { key: 'expense', label: '支出', colorToken: '--color-expense', values: rows.map((row) => row.expense) },
    ],
    description:
      `${periodLabel.value}每月收入与支出：收入合计 ${formatAmount(totals.value.income)}，` +
      `支出合计 ${formatAmount(totals.value.expense)}，净额 ${formatSigned(totals.value.net)}${suffix}。`,
  }
})

/** 分类饼图的输入：延迟到结果回来再取值，未查过时为空数组。 */
const categoryIncome = computed(() => report.value?.categories.income ?? [])
const categoryExpense = computed(() => report.value?.categories.expense ?? [])

/**
 * 把标签合计折成饼图能吃的形状。
 *
 * **不是把标签当分类**：两者在饼图里都只是「一个可空主键 + 名称 + 金额」，
 * `categoryPie.ts` 已经把「空值分项叫什么」参数化成了 `nullLabel`，形状与折叠规则完全复用。
 * 这个适配层因此只做字段改名，并把标签维度下的 `null` 解读为「未打标签」（由 `nullLabel` 呈现）。
 */
function toPieItems(items: readonly ReportTagTotal[]): EntryCategoryTotal[] {
  return items.map((item) => ({
    categoryId: item.tagId,
    categoryName: item.tagName,
    amount: item.amount,
  }))
}

const tagIncome = computed(() => toPieItems(report.value?.tags.income ?? []))
const tagExpense = computed(() => toPieItems(report.value?.tags.expense ?? []))

/**
 * 拉取三份筛选候选：账户、币种（取系统默认）、标签。
 *
 * 三者是页面能不能查的前置条件，故用一次 `Promise.all` 一并等待；任何一份失败都返回
 * `false`，调用方据此不发起查询——候选没就绪时发出的请求必然是一个猜出来的条件。
 */
async function loadFilters(): Promise<boolean> {
  try {
    await Promise.all([accountsStore.list(true), currenciesStore.loadActive(), tagsStore.list(true)])
    return true
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载筛选条件失败'
    return false
  }
}

/** 账户全选。 */
function selectAllAccounts(): void {
  selectedAccountIds.value = moneyAccountOptions.value.map((account) => account.id)
}

/** 账户全不选。 */
function clearAccounts(): void {
  selectedAccountIds.value = []
}

/** 标签改为「不限」。 */
function clearTags(): void {
  selectedTagIds.value = []
}

/**
 * 按草稿条件查询。
 *
 * 顺序是「先校验、再清空、后请求」：
 * - 校验不过**不清空**——已经查出来的结果仍然对应当前页面上的条件说明，清掉只是白丢一份数据；
 * - 请求前清空 store：新区间的结果回来之前，界面上不能留着上一个区间的数字配着新区间的标题，
 *   失败时也不至于把旧数据摆在一个写着「加载失败」的页面里。
 */
async function apply(): Promise<void> {
  if (!hasAccountSet.value) {
    return
  }

  if (rangeError.value !== '') {
    filterError.value = rangeError.value
    return
  }

  if (selectedAccountIds.value.length === 0) {
    filterError.value = '请至少选择一个账户。'
    return
  }

  filterError.value = ''
  errorMessage.value = ''
  monthlyReport.clear()

  try {
    await monthlyReport.loadReport({
      from: fromMonth.value,
      to: toMonth.value,
      accountIds: selectedAccountIds.value,
      tagIds: selectedTagIds.value,
    })
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载月报表失败'
  }
}

/**
 * 复位为「过去 12 个月（含当月）+ 账户全选 + 不限标签」并立即查询。
 *
 * 这是【重置】按钮的行为，也是每次进入页面与切换账套后的初始视图；
 * **筛选条件不保存**：月报表的区间是一次性的读法，下次进入就是新的近 12 个月。
 */
function resetToDefault(): void {
  fromMonth.value = monthsAgoLocal(DEFAULT_REPORT_MONTHS - 1)
  toMonth.value = currentMonthLocal()
  selectedAccountIds.value = moneyAccountOptions.value.map((account) => account.id)
  selectedTagIds.value = []
  void apply()
}

/**
 * 账套切换（含首次挂载）后的取数流程。
 *
 * 三件事都要重来：候选（账户与标签都按账套隔离）、勾选（旧账套的主键在新账套里指向别的对象）、
 * 结果（按账套隔离并进一步按用户分行）。未选择账套时清空结果，避免退出登录后仍残留上一份报表。
 */
async function switchAccountSet(currentId: number | null): Promise<void> {
  monthlyReport.clear()
  errorMessage.value = ''
  filterError.value = ''
  selectedAccountIds.value = []
  selectedTagIds.value = []

  if (currentId === null) {
    return
  }

  const ready = await loadFilters()
  if (!ready) {
    return
  }

  selectedAccountIds.value = moneyAccountOptions.value.map((account) => account.id)
  await apply()
}

// 账套切换即重查；`watch` 只在值真的变化时触发，首次取数由 `onMounted` 负责。
watch(
  () => accountSets.currentId,
  (currentId) => {
    void switchAccountSet(currentId)
  },
)

/**
 * 默认币种变化（管理员改了默认币种）时，账户候选集合会变。
 *
 * 已在勾选里的账户未必仍属于新币种，静默保留会让一次查询悄悄漏掉一半账户；
 * 这里只做「把仍可选的账户留下、若全部落空则回到全选」的收敛，**不自动重查**——
 * 用户可能已经改好了月份正在准备点【查询】。
 */
watch(
  () => currenciesStore.defaultCode,
  () => {
    const available = new Set(moneyAccountOptions.value.map((account) => account.id))
    const kept = selectedAccountIds.value.filter((id) => available.has(id))
    selectedAccountIds.value =
      kept.length > 0 ? kept : moneyAccountOptions.value.map((account) => account.id)
  },
)

onMounted(() => {
  void switchAccountSet(accountSets.currentId)
})
</script>

<template>
  <main class="report">
    <header class="head">
      <div>
        <h1 class="title">月报表</h1>
        <p class="subtitle">
          按所选月份区间、账户与标签，实时汇总当前账套的收支明细：按分类与按标签各出收入 / 支出
          构成饼图，按月给出收入与支出走势，并逐月列出收入、支出与净额及区间合计。
          只统计系统默认币种下公共账户与本人个人账户中的资金 / 负债账户，转账与期初余额不计入。
          一笔交易挂多个标签时，其金额会在每个标签下各计一次，故标签金额之和可能大于收支合计。
        </p>
      </div>
    </header>

    <p v-if="!hasAccountSet" class="hint">
      当前未选择账套，请先点击右上角的【切换】选择账套后再查看月报表。
    </p>

    <template v-else>
      <section class="panel">
        <div class="filter-row">
          <label class="field">
            <span class="label">起始月份</span>
            <input
              v-model="fromMonth"
              type="month"
              :max="toMonth || undefined"
              :disabled="monthlyReport.loading"
            />
          </label>

          <label class="field">
            <span class="label">结束月份</span>
            <input
              v-model="toMonth"
              type="month"
              :min="fromMonth || undefined"
              :disabled="monthlyReport.loading"
            />
          </label>
        </div>

        <p class="panel-note">
          月份区间为闭区间（含起止两个月整月），默认过去 {{ DEFAULT_REPORT_MONTHS }} 个月（含当月），
          最多 {{ MAX_REPORT_MONTHS }} 个月；月份按<strong>服务器本地时区</strong>切分，与账目明细页同一口径。
          当月尚未结束，本月的数字是截至今天的已发生额。
        </p>
      </section>

      <section class="picker">
        <div class="picker-head">
          <span class="label">账户（可多选）</span>
          <span class="picker-count">
            已选 {{ selectedAccountIds.length }} / {{ moneyAccountOptions.length }}
          </span>
          <div class="picker-actions">
            <button
              type="button"
              class="ghost"
              :disabled="moneyAccountOptions.length === 0 || monthlyReport.loading"
              @click="selectAllAccounts"
            >
              全选
            </button>
            <button
              type="button"
              class="ghost"
              :disabled="selectedAccountIds.length === 0 || monthlyReport.loading"
              @click="clearAccounts"
            >
              全不选
            </button>
          </div>
        </div>

        <div class="picker-list">
          <label v-for="account in moneyAccountOptions" :key="account.id" class="picker-item">
            <input v-model="selectedAccountIds" type="checkbox" :value="account.id" />
            <span class="picker-name">{{ account.name }}</span>
            <!-- 已停用账户照常可选：历史明细仍挂在它上面，且其明细只会出现在它被选中时 -->
            <span v-if="!account.isActive" class="badge badge-inactive">已停用</span>
          </label>
          <p v-if="moneyAccountOptions.length === 0" class="picker-empty">
            当前账套内没有可统计的资金 / 负债账户。
          </p>
        </div>

        <p class="picker-hint">
          只列出系统默认币种（{{ currenciesStore.defaultCode ?? '—' }}）下的资金 / 负债账户：
          报表口径固定为默认币种，其他币种的账户不会被计入。账本账户与往来账户也不在列表中——
          前者是系统内部账户，后者记的是「谁欠谁」而不是「钱放在哪」。
        </p>
      </section>

      <section class="picker">
        <div class="picker-head">
          <span class="label">标签（可多选，不选即不限）</span>
          <span class="picker-count">已选 {{ selectedTagIds.length }} / {{ tagOptions.length }}</span>
          <div class="picker-actions">
            <button
              type="button"
              class="ghost"
              :disabled="selectedTagIds.length === 0 || monthlyReport.loading"
              @click="clearTags"
            >
              不限
            </button>
          </div>
        </div>

        <div class="picker-list">
          <label v-for="tag in tagOptions" :key="tag.id" class="picker-item">
            <input v-model="selectedTagIds" type="checkbox" :value="tag.id" />
            <span class="picker-name">{{ tag.name }}</span>
            <!-- 已停用标签照常可选：停用是「不再供新记账选择」，不是「历史上从未用过」 -->
            <span v-if="!tag.isActive" class="badge badge-inactive">已停用</span>
          </label>
          <p v-if="tagOptions.length === 0" class="picker-empty">当前账套内还没有标签。</p>
        </div>

        <p class="picker-hint">
          不勾选任何标签即<strong>不限标签</strong>；勾选多个时是<strong>任一命中</strong>。
          标签与账户是<strong>且</strong>的关系：两个维度各自收窄。筛选只影响统计范围，
          不影响金额的记账方式——它挂在整笔交易上，故该笔交易的两条明细同进同出。
        </p>
      </section>

      <div class="actions">
        <button type="button" class="submit" :disabled="monthlyReport.loading" @click="apply()">
          {{ monthlyReport.loading ? '查询中…' : '查询' }}
        </button>
        <button type="button" class="ghost" :disabled="monthlyReport.loading" @click="resetToDefault">
          重置
        </button>
        <span class="actions-note">条件改动后点【查询】生效；月份区间与勾选不会保存。</span>
      </div>

      <p v-if="filterError !== ''" class="error">{{ filterError }}</p>
      <p v-if="errorMessage !== ''" class="error">{{ errorMessage }}</p>

      <template v-if="report !== null">
        <p v-if="currencyCode === null" class="hint">
          还没有可用的币种，请先在管理端添加币种并记账后再查看月报表。
        </p>
        <p v-else-if="!hasData" class="hint">{{ periodLabel }}内还没有收入与支出明细。</p>

        <template v-else>
          <section class="panel">
            <div>
              <h2 class="panel-title">{{ periodLabel }}分类构成</h2>
              <p class="panel-note">
                收入与支出各占一张饼图，百分比以本侧合计为分母。按交易头上的那一个分类归集；
                「未分类」是合法归集结果，单独成扇区，金额小的其余分类合并为「其他」。
              </p>
            </div>

            <div class="pie-grid">
              <CategoryPieChart
                title="收入分类构成"
                :items="categoryIncome"
                :currency-code="currencyCode"
                :period="periodLabel"
                empty-text="区间内没有收入。"
              />
              <CategoryPieChart
                title="支出分类构成"
                :items="categoryExpense"
                :currency-code="currencyCode"
                :period="periodLabel"
                empty-text="区间内没有支出。"
              />
            </div>
          </section>

          <section class="panel">
            <div>
              <h2 class="panel-title">{{ periodLabel }}标签构成</h2>
              <p class="panel-note">
                与上方分类构成同一口径，只是按标签归集。<strong
                  >一笔交易挂多个标签时，其金额在每个标签下各计一次</strong
                >（金额不摊分），故各标签金额之和可能大于区间收支合计；
                没有标签的交易归入「{{ UNTAGGED_LABEL }}」，它同样是合法的归集结果。
              </p>
            </div>

            <div class="pie-grid">
              <CategoryPieChart
                title="收入标签构成"
                :items="tagIncome"
                :currency-code="currencyCode"
                :period="periodLabel"
                :null-label="UNTAGGED_LABEL"
                empty-text="区间内没有收入。"
              />
              <CategoryPieChart
                title="支出标签构成"
                :items="tagExpense"
                :currency-code="currencyCode"
                :period="periodLabel"
                :null-label="UNTAGGED_LABEL"
                empty-text="区间内没有支出。"
              />
            </div>
          </section>

          <section class="panel">
            <div>
              <h2 class="panel-title">{{ periodLabel }}收支走势</h2>
              <p class="panel-note">
                按月份逐月汇总，横轴是区间内的每一个月：某个月没有收入与支出的补 0 呈现
                （实时数据下空白月就是 0，与首页结算图表的「缺日不补零」不同）。
              </p>
            </div>

            <div class="stats">
              <div class="stat">
                <span class="stat-label">收入</span>
                <strong class="stat-value income">{{ formatAmount(totals.income) }}</strong>
              </div>
              <div class="stat">
                <span class="stat-label">支出</span>
                <strong class="stat-value expense">{{ formatAmount(totals.expense) }}</strong>
              </div>
              <div class="stat">
                <span class="stat-label">净额</span>
                <strong class="stat-value" :class="signedCellClass(totals.net, 'net')">
                  {{ formatSigned(totals.net) }}
                </strong>
              </div>
              <p class="stat-note">
                区间合计，币种 {{ currencyCode }}；按账目明细的发生额实时汇总。
              </p>
            </div>

            <template v-if="chart !== null">
              <div class="legend">
                <span v-for="item in chart.series" :key="item.key" class="legend-item">
                  <i class="swatch" :style="{ backgroundColor: `var(${item.colorToken})` }" />
                  {{ item.label }}
                </span>
                <span class="legend-meta">{{ periodLabel }} · 共 {{ monthlyRows.length }} 个月</span>
              </div>

              <div class="chart">
                <TrendChart
                  :series="chart.series"
                  :x-labels="chart.xLabels"
                  :description="chart.description"
                />
              </div>
            </template>
          </section>

          <section class="panel">
            <div>
              <h2 class="panel-title">{{ periodLabel }}按月明细</h2>
              <p class="panel-note">
                区间内每个月各占一行，没有发生额的月份收入与支出两列显示「—」；
                底部合计行由收入与支出分别相加，净额 = 收入合计 − 支出合计。
              </p>
            </div>

            <!--
              宽窄两份呈现并存，由 1023px 断点切换（全站唯一的断点）：
              四列在手机上会把月份与金额挤在一起，故窄屏改成「月份 + 净额」在上、
              「收入 / 支出」在下的卡片。`display: none` 是双呈现手段，不是死代码。
            -->
            <table class="monthly-table">
              <thead>
                <tr>
                  <th scope="col">月份</th>
                  <th scope="col" class="amount">收入</th>
                  <th scope="col" class="amount">支出</th>
                  <th scope="col" class="amount">净额</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="row in monthlyRows" :key="row.month">
                  <td class="month">{{ row.month }}</td>
                  <td class="amount income">
                    {{ row.income === 0 ? '—' : formatUnsigned(row.income) }}
                  </td>
                  <td class="amount expense">
                    {{ row.expense === 0 ? '—' : formatUnsigned(row.expense) }}
                  </td>
                  <td class="amount" :class="signedCellClass(row.net, 'net')">
                    {{ formatSigned(row.net) }}
                  </td>
                </tr>
              </tbody>
              <tfoot>
                <tr>
                  <td class="total-label">合计</td>
                  <td class="amount income">
                    {{ totals.income === 0 ? '—' : formatUnsigned(totals.income) }}
                  </td>
                  <td class="amount expense">
                    {{ totals.expense === 0 ? '—' : formatUnsigned(totals.expense) }}
                  </td>
                  <td class="amount" :class="signedCellClass(totals.net, 'net')">
                    {{ formatSigned(totals.net) }}
                  </td>
                </tr>
              </tfoot>
            </table>

            <ul class="cards">
              <li v-for="row in monthlyRows" :key="row.month" class="card">
                <div class="card-head">
                  <span class="card-month">{{ row.month }}</span>
                  <strong class="card-net" :class="signedCellClass(row.net, 'net')">
                    {{ formatSigned(row.net) }}
                  </strong>
                </div>
                <div class="card-meta">
                  <span class="income">
                    收入 {{ row.income === 0 ? '—' : formatUnsigned(row.income) }}
                  </span>
                  <span class="expense">
                    支出 {{ row.expense === 0 ? '—' : formatUnsigned(row.expense) }}
                  </span>
                </div>
              </li>
              <li class="card card-total">
                <div class="card-head">
                  <span class="card-month">合计</span>
                  <strong class="card-net" :class="signedCellClass(totals.net, 'net')">
                    {{ formatSigned(totals.net) }}
                  </strong>
                </div>
                <div class="card-meta">
                  <span class="income">
                    收入 {{ totals.income === 0 ? '—' : formatUnsigned(totals.income) }}
                  </span>
                  <span class="expense">
                    支出 {{ totals.expense === 0 ? '—' : formatUnsigned(totals.expense) }}
                  </span>
                </div>
              </li>
            </ul>
          </section>
        </template>
      </template>

      <p v-else-if="monthlyReport.loading" class="hint">加载中…</p>
    </template>
  </main>
</template>

<style scoped>
.report {
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

.error {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-danger-border);
  border-radius: var(--radius-control);
  background: var(--color-danger-soft);
  color: var(--color-danger);
  font-size: 13px;
  line-height: 1.7;
}

/* 面板：与日报表 / 首页走势图同一套描边 + 卡片阴影 */
.panel {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
  padding: 1rem 1.1rem 0.85rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

.panel-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-heading);
  opacity: 0.85;
}

.panel-note {
  margin-top: 0.25rem;
  font-size: 12.5px;
  line-height: 1.7;
  opacity: 0.7;
}

/* 月份字段与操作按钮同处一行，窄屏下自动折行 */
.filter-row {
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

.field input:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* 账户 / 标签多选面板：受边框约束的独立区域，与结果区区分开（与明细页同构） */
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

/* 全选 / 全不选 / 不限靠右固定，与左侧说明拉开 */
.picker-actions {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin-left: auto;
}

/* 候选多时只在本区域内滚动，不把页面撑长 */
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

/* 操作行：查询是页面的主操作，说明跟在按钮之后 */
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.6rem;
}

.actions-note {
  font-size: 12.5px;
  opacity: 0.7;
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

/* 实心主色按钮：与明细页的【查询】同源 */
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
 * 两张饼图并排：每列至少 220px，容器不足时自动折成一列。
 * 用 `auto-fit` 而不是再写一个断点——与首页同一写法，同一套宽度规则对两种容器都成立。
 */
.pie-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(220px, 100%), 1fr));
  gap: 1rem 1.25rem;
  align-items: start;
}

/* 概览数字：三个并排，窄屏自动折行；口径小字独占一行 */
.stats {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem 1.75rem;
}

.stat {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
}

.stat-label {
  font-size: 12.5px;
  opacity: 0.7;
}

.stat-value {
  font-size: 18px;
  font-variant-numeric: tabular-nums;
  color: var(--color-heading);
}

.stat-note {
  flex-basis: 100%;
  font-size: 12.5px;
  line-height: 1.7;
  opacity: 0.7;
}

.legend {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem 1rem;
  font-size: 12.5px;
}

.legend-item {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  opacity: 0.85;
}

.swatch {
  width: 10px;
  height: 10px;
  border-radius: 50%;
}

.legend-meta {
  margin-left: auto;
  opacity: 0.65;
}

.chart {
  height: 220px;
}

/* 按月明细表：金额列右对齐、等宽数字；行之间只用一条细线分隔 */
.monthly-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}

.monthly-table th,
.monthly-table td {
  padding: 0.5rem 0.6rem;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
  line-height: 1.6;
}

.monthly-table th {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--color-heading);
  opacity: 0.75;
  white-space: nowrap;
}

.monthly-table tbody tr:last-child td {
  border-bottom: none;
}

/* 合计行：用上边框与底色把它和逐月行分开，读作「汇总」而不是「又一个月」 */
.monthly-table tfoot td {
  padding: 0.6rem 0.6rem;
  border-top: 1px solid var(--color-border-hover);
  background: var(--color-background-mute);
  font-size: 13px;
  font-weight: 600;
}

.monthly-table tfoot tr:last-child td {
  border-bottom: none;
}

.month,
.total-label {
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.total-label {
  font-weight: 600;
  opacity: 0.85;
}

.amount {
  text-align: right;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

/* 收支语义色：收支两列按颜色区分方向，净额列正负都着色（与明细页同一套词汇） */
.income {
  color: var(--color-income);
}

.expense {
  color: var(--color-expense);
}

.amount-unknown {
  color: var(--color-danger);
}

/* 窄屏卡片流：默认隐藏，由 1023px 断点切换 */
.cards {
  display: none;
  margin: 0;
  padding: 0;
  list-style: none;
  flex-direction: column;
  gap: 0.6rem;
}

.card {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  padding: 0.65rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background);
}

/* 合计卡与表格的合计行同源：加一层底色，读作「汇总」而不是「又一个月」 */
.card-total {
  background: var(--color-background-mute);
  font-weight: 600;
}

.card-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 0.6rem;
}

.card-month {
  font-size: 13px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
}

.card-net {
  flex: none;
  font-size: 15px;
  font-variant-numeric: tabular-nums;
}

.card-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem 1rem;
  font-size: 12.5px;
  font-variant-numeric: tabular-nums;
}

.badge {
  display: inline-block;
  padding: 0.15rem 0.5rem;
  border-radius: 999px;
  border: 1px solid transparent;
  font-size: 12px;
  white-space: nowrap;
}

.badge-inactive {
  border-color: var(--color-danger-border);
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

/* 全站唯一的断点：此处切换「表格 / 卡片」两份呈现 */
@media (max-width: 1023px) {
  .monthly-table {
    display: none;
  }

  .cards {
    display: flex;
  }

  .chart {
    height: 180px;
  }
}
</style>
