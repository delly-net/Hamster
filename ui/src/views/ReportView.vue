<script setup lang="ts">
/**
 * /reports 页面：月报表。
 *
 * 在「账目明细」的实时口径上再多一层**按月**的读法：选定一个自然月，回答三件事——
 * 这个月一共赚了多少、花了多少（月度汇总）；每天各是多少（按日双折线）；
 * 钱花在哪些分类上（收入 / 支出按分类合并的汇总表）。
 *
 * **数据是实时聚合的，不是结算快照**：按日收支来自 `GET /api/entries/daily-flows`，
 * 分类汇总来自 `GET /api/entries/category-totals`，两者都由后端直接统计交易明细，
 * 与账目明细页同源，含尚未结算的日期。首页那两张走势图读的是结算订阅落库的按天记录，
 * 二者口径相同（默认币种、公共 + 本人个人、资金 / 负债账户、只计收入与支出），
 * 差别只在「实时 / 已结算」——本页不复用结算接口，否则今天就看不到今天的收支。
 *
 * **折线的横轴是整月每一天**：后端只返回「真的发生过收支」的日期（它不补零），
 * 本页把所选月份 1 日到月末缺的日期补成 0。这与首页结算图表的「缺日不补零」**刻意不同**：
 * 那里的空白日表示「还没结算」，补 0 会把「没算」画成「就是 0」；而本页的数据源是实时的，
 * 空白日的含义就是「当天没有收入与支出」，0 是事实。补零只发生在**呈现层**，
 * 后端接口仍如实给出有发生额的日期。
 *
 * **分类汇总表复用 `category-totals`**：它与按日接口的口径逐条一致，没必要为同一份聚合再造一个端点。
 * 页面把收入侧与支出侧按分类主键合并成一张表（`categoryId = null` 即「未分类」）并按两侧合计降序，
 * 于是「这个月哪一类进出最多」在同一行里直接可读。金额格式仍走 `formatUnsigned` / `formatSigned`
 * ——收入、支出列显示正数，只有净额列保留符号（与账目明细页同一约定）。
 *
 * **月份默认浏览器本地当前月**，不沿用首页「每月 1 号看上月」的规则（#92 明确只作用于首页）；
 * 切换月份或账套都会重新取数。页面不提供账户 / 标签筛选：报表口径固定为
 * 「公共 + 本人个人、系统默认币种、资金 / 负债账户」，逐账户的筛选留给账目明细页。
 *
 * 两块数据**各自加载、各自失败**：分类汇总接口的一次超时，没有理由让已经查得到的按日曲线变成空白。
 * 窄屏（<1024px）下汇总表切换为卡片流，沿用全站唯一的 1023px 断点。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import TrendChart from '@/components/TrendChart.vue'
import { formatAmount, formatSigned, formatUnsigned, signedCellClass } from '@/components/entryFormat'
import type { TrendSeriesSpec } from '@/components/trendChart'
import { useAccountSetsStore } from '@/stores/accountSets'
import { useCategoryTotalsStore, type EntryCategoryTotal } from '@/stores/categoryTotals'
import { useDailyFlowsStore } from '@/stores/dailyFlows'

const accountSets = useAccountSetsStore()
const dailyFlows = useDailyFlowsStore()
const categoryTotals = useCategoryTotalsStore()

/** 是否已选定账套；未选定时页面只提示、不请求（三块数据都按账套隔离）。 */
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

/** 当前查看的月份（`yyyy-MM`）。`<input type="month">` 的原生取值格式即此。 */
const selectedMonth = ref(currentMonthLocal())

/** 两块数据各自的失败文案；无失败时为空串。 */
const flowError = ref('')
const categoryError = ref('')

/** 月份文案（`2026年10月`）；格式异常时原样回显。 */
const periodLabel = computed(() => {
  const [year, monthPart] = selectedMonth.value.split('-')
  if (year === undefined || monthPart === undefined) {
    return selectedMonth.value
  }

  return `${year}年${Number(monthPart)}月`
})

/**
 * 所选月份的天数；月份非法时为 0（此时不画图）。
 *
 * `new Date(y, m, 0)` 取的是「第 m 个月的第 0 天」，即上一个月的最后一天——
 * 传入 1 起始的月份号刚好得到该月天数，二月与闰年都由平台处理，不写查表。
 */
const daysInMonth = computed(() => {
  const [yearPart, monthPart] = selectedMonth.value.split('-')
  const year = Number(yearPart)
  const month = Number(monthPart)
  if (!Number.isFinite(year) || !Number.isFinite(month) || month < 1 || month > 12) {
    return 0
  }

  return new Date(year, month, 0).getDate()
})

/** 按日接口的响应；未查过时为 `null`。 */
const flow = computed(() => dailyFlows.daily)

/**
 * 月度汇总：由后端给出的按日数据相加。
 *
 * 与首页「收支」面板的合计同一算法（#75）：区间净额 = 收入合计 − 支出合计，
 * 不逐日累加 `netTotal`——两者数学上相等，但前者少一层可以把符号弄反的机会。
 */
const totals = computed(() => {
  let income = 0
  let expense = 0
  for (const point of flow.value?.days ?? []) {
    income += point.incomeTotal
    expense += point.expenseTotal
  }

  return { income, expense, net: income - expense }
})

/** 一个月里的某一天（`M月D日`）；`<input type="month">` 的月份部分用于拼横轴文案。 */
function dayLabel(monthPart: string, day: number): string {
  return `${Number(monthPart)}月${day}日`
}

/** 折线图的呈现数据；没有可画的数据时为 `null`。 */
interface ChartView {
  xLabels: string[]
  series: TrendSeriesSpec[]
  ariaLabel: string
}

/**
 * 按日折线：把后端「只含有发生额的日期」补齐成整月每一天。
 *
 * 补 0 的理由见文件头：数据源是实时的，空白日就是真实的 0。
 * 隐藏日（如 2 月 30 日）不会出现——`daysInMonth` 已经按真实月长算好。
 */
const chart = computed<ChartView | null>(() => {
  const data = flow.value
  if (data === null || data.currencyCode === null || data.days.length === 0) {
    return null
  }

  const count = daysInMonth.value
  const [yearPart, monthPart] = selectedMonth.value.split('-')
  if (count <= 0 || monthPart === undefined) {
    return null
  }

  const byDate = new Map(data.days.map((point) => [point.date, point]))
  const xLabels: string[] = []
  const incomeValues: number[] = []
  const expenseValues: number[] = []

  for (let day = 1; day <= count; day += 1) {
    const date = `${yearPart}-${pad(day)}`
    const point = byDate.get(date)
    xLabels.push(dayLabel(monthPart, day))
    incomeValues.push(point?.incomeTotal ?? 0)
    expenseValues.push(point?.expenseTotal ?? 0)
  }

  return {
    xLabels,
    series: [
      { key: 'income', label: '收入', colorToken: '--color-income', values: incomeValues },
      { key: 'expense', label: '支出', colorToken: '--color-expense', values: expenseValues },
    ],
    ariaLabel:
      `${periodLabel.value}每日收入与支出：收入合计 ${formatAmount(totals.value.income)}，` +
      `支出合计 ${formatAmount(totals.value.expense)}，` +
      `净额 ${formatSigned(totals.value.net)}，币种 ${data.currencyCode}。`,
  }
})

/** 分类汇总表的一行：同一分类的收入、支出与净额。 */
interface SummaryRow {
  /** 行标识：分类主键（未分类用固定串）。 */
  key: string
  /** 分类名称；未分类为「未分类」。 */
  name: string
  /** 该分类的收入合计，恒为非负；没有收入时为 0。 */
  income: number
  /** 该分类的支出合计，恒为非负；没有支出时为 0。 */
  expense: number
  /** 净额 = 收入 − 支出（带符号）。 */
  net: number
}

/**
 * 把分类合计的收入侧与支出侧按分类主键合并成一张表。
 *
 * **不能直接拼接两个列表**：同一个分类可能既有收入又有支出（如「退款」），
 * 分开列会让人以为它们是两类东西。`categoryId = null` 是「未分类」这一合法归集结果，
 * 用固定 key 合并，不与任何真实分类冲突。
 *
 * 排序按两侧合计（进出总额）降序：月报表回答的是「哪一类动得最多」，
 * 只按收入或只按支出排都会把另一半的动静藏起来；同额按名称升序保证行序稳定。
 */
const summaryRows = computed<SummaryRow[]>(() => {
  const data = categoryTotals.totals
  if (data === null) {
    return []
  }

  const rows = new Map<string, SummaryRow>()

  function accumulate(item: EntryCategoryTotal, side: 'income' | 'expense'): void {
    const key = item.categoryId === null ? 'uncategorized' : `category-${item.categoryId}`
    const row = rows.get(key) ?? {
      key,
      name: item.categoryName ?? '未分类',
      income: 0,
      expense: 0,
      net: 0,
    }

    if (side === 'income') {
      row.income += item.amount
    } else {
      row.expense += item.amount
    }

    rows.set(key, row)
  }

  for (const item of data.income) {
    accumulate(item, 'income')
  }

  for (const item of data.expense) {
    accumulate(item, 'expense')
  }

  return [...rows.values()]
    .map((row) => ({ ...row, net: row.income - row.expense }))
    .sort(
      (left, right) =>
        right.income + right.expense - (left.income + left.expense) ||
        left.name.localeCompare(right.name, 'zh-CN'),
    )
})

/** 分类汇总表的币种；未查过或没有可用币种时为 `null`。 */
const categoryCurrencyCode = computed(() => categoryTotals.totals?.currencyCode ?? null)

/**
 * 拉取按日收支与分类汇总。
 *
 * 两个请求**并发发出、各自记账自己的失败**：串行会让下面一块白等上面的网络往返，
 * 而 `Promise.all` 的「一票否决」语义会让其中一次失败把另一块已有数据也清成错误
 * （同首页四块面板的取舍）。
 */
async function load(): Promise<void> {
  if (!hasAccountSet.value || selectedMonth.value === '') {
    return
  }

  flowError.value = ''
  categoryError.value = ''
  // 先清空再取数：新月份的结果回来之前，界面上不能留着上个月的数字配着新月份的标题；
  // 失败时也不至于把旧数据摆在一个写着「加载失败」的页面里
  dailyFlows.clear()
  categoryTotals.clear()

  await Promise.all([
    dailyFlows.loadDaily(selectedMonth.value).catch((error: unknown) => {
      flowError.value = error instanceof ApiError ? error.message : '加载按日收支失败'
    }),
    categoryTotals.loadTotals(selectedMonth.value).catch((error: unknown) => {
      categoryError.value = error instanceof ApiError ? error.message : '加载分类汇总失败'
    }),
  ])
}

// 月份变化即重查；清空输入（原生 month 控件允许）时回到当前月，而不是带着空月份请求。
watch(selectedMonth, (value) => {
  if (value === '') {
    selectedMonth.value = currentMonthLocal()
    return
  }

  void load()
})

// 账套切换：两块数据都按账套隔离（且按用户分行），必须清空后重查；
// 未选择账套时清空，避免退出登录后仍残留上一个账套的报表。
watch(
  () => accountSets.currentId,
  (currentId) => {
    dailyFlows.clear()
    categoryTotals.clear()
    flowError.value = ''
    categoryError.value = ''

    if (currentId === null) {
      return
    }

    void load()
  },
)

onMounted(() => {
  if (hasAccountSet.value) {
    void load()
  }
})
</script>

<template>
  <main class="report">
    <header class="head">
      <div>
        <h1 class="title">月报表</h1>
        <p class="subtitle">
          按所选月份的账目明细实时汇总：月度合计、每日收支走势与分类汇总。
          只统计系统默认币种与资金 / 负债账户（含公共账户与本人在本账套的个人账户），
          转账与期初余额不计入。
        </p>
      </div>
      <label class="month-field">
        <span class="month-label">月份</span>
        <input v-model="selectedMonth" type="month" :disabled="!hasAccountSet" />
      </label>
    </header>

    <p v-if="!hasAccountSet" class="hint">
      请先在右上角选择账套，月报表展示的是当前账套里的收支。
    </p>

    <template v-else>
      <section class="panel">
        <div>
          <h2 class="panel-title">{{ periodLabel }}收支走势</h2>
          <p class="panel-note">
            按{{ periodLabel }}账目明细的发生额实时汇总，含尚未结算的日期。
            折线横轴是整月的每一天：当天没有收入与支出的日期补 0 呈现
            （实时数据下空白日就是 0，与首页结算图表的「缺日不补零」不同）。
          </p>
        </div>

        <p v-if="flowError !== ''" class="error">{{ flowError }}</p>
        <p v-else-if="flow === null" class="hint">加载中…</p>
        <p v-else-if="flow.currencyCode === null" class="hint">
          还没有可用的币种，请先在管理端添加币种后记账。
        </p>
        <p v-else-if="chart === null" class="hint">{{ periodLabel }}还没有收入与支出明细。</p>

        <template v-else>
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
              {{ periodLabel }}合计，币种 {{ flow.currencyCode }}；按账目明细的发生额实时汇总。
            </p>
          </div>

          <div class="legend">
            <span v-for="item in chart.series" :key="item.key" class="legend-item">
              <i class="swatch" :style="{ backgroundColor: `var(${item.colorToken})` }" />
              {{ item.label }}
            </span>
            <span class="legend-meta">{{ periodLabel }} · 共 {{ daysInMonth }} 天</span>
          </div>

          <div class="chart">
            <TrendChart
              :series="chart.series"
              :x-labels="chart.xLabels"
              :description="chart.ariaLabel"
            />
          </div>
        </template>
      </section>

      <section class="panel">
        <div>
          <h2 class="panel-title">{{ periodLabel }}分类汇总</h2>
          <p class="panel-note">
            与上方走势同一口径；收入与支出按分类合并成一行，净额 = 收入 − 支出。
            「未分类」是合法归集结果，单独成行。金额单位：{{ categoryCurrencyCode ?? '—' }}。
          </p>
        </div>

        <p v-if="categoryError !== ''" class="error">{{ categoryError }}</p>
        <p v-else-if="categoryTotals.totals === null" class="hint">加载中…</p>
        <p v-else-if="categoryCurrencyCode === null" class="hint">
          还没有可用的币种，请先在管理端添加币种后记账。
        </p>
        <p v-else-if="summaryRows.length === 0" class="hint">
          {{ periodLabel }}还没有收入与支出明细。
        </p>

        <!--
          宽窄两份呈现并存，由 1023px 断点切换（全站唯一的断点）：
          4 列在手机上仍会把分类名压得只剩一两个字，故窄屏改成「分类 + 净额」在上、
          「收入 / 支出」在下的卡片。`display: none` 是双呈现手段，不是死代码。
        -->
        <table v-else class="summary-table">
          <thead>
            <tr>
              <th scope="col">分类</th>
              <th scope="col" class="amount">收入</th>
              <th scope="col" class="amount">支出</th>
              <th scope="col" class="amount">净额</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in summaryRows" :key="row.key">
              <td class="category">{{ row.name }}</td>
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
        </table>

        <ul v-if="summaryRows.length > 0" class="cards">
          <li v-for="row in summaryRows" :key="row.key" class="card">
            <div class="card-head">
              <span class="card-name">{{ row.name }}</span>
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
        </ul>
      </section>
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

/* 月份选择：与明细页的日期字段同源（同一套描边、圆角、聚焦环） */
.month-field {
  display: flex;
  flex: none;
  flex-direction: column;
  gap: 0.35rem;
  min-width: 9rem;
}

.month-label {
  font-size: 12.5px;
  opacity: 0.75;
}

.month-field input {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  box-shadow: var(--shadow-control);
}

.month-field input:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

.month-field input:disabled {
  opacity: 0.5;
  cursor: not-allowed;
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

/* 面板：与首页走势图 / 明细页筛选区同一套描边 + 卡片阴影 */
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

/* 分类汇总表：金额列右对齐、等宽数字；行之间只用一条细线分隔 */
.summary-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}

.summary-table th,
.summary-table td {
  padding: 0.5rem 0.6rem;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
  line-height: 1.6;
}

.summary-table th {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--color-heading);
  opacity: 0.75;
  white-space: nowrap;
}

.summary-table tbody tr:last-child td {
  border-bottom: none;
}

.category {
  font-weight: 500;
}

.amount {
  text-align: right;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

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

.card-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 0.75rem;
}

.card-name {
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-net {
  flex: none;
  font-variant-numeric: tabular-nums;
}

.card-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem 1rem;
  font-size: 12.5px;
  opacity: 0.85;
}

@media (max-width: 1023px) {
  .head {
    flex-direction: column;
  }

  .month-field {
    width: 100%;
  }

  .chart {
    height: 180px;
  }

  /* 表格与卡片流是同一份数据的双呈现：窄屏只留卡片 */
  .summary-table {
    display: none;
  }

  .cards {
    display: flex;
  }
}
</style>
