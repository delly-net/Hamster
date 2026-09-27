<script setup lang="ts">
/**
 * `/` 首页（概览）：当前账套本月的**总资产走势**与**收支走势**两个面板。
 *
 * 两块数据都来自后端落库的**按天记录**（总资产结算订阅、收入结算订阅、支出结算订阅各自在结算事件后
 * 逐日重算），本页**不做任何金额汇总**——「哪些账户算资产、哪些算负债、个人与公共如何相加、
 * 收支的符号」全部由后端一处定义（见 `ITotalAssetSettlementService` / `IIncomeSettlementService` /
 * `IExpenseSettlementService`），前端再算一遍就会出现「首页的曲线与账户页、明细页对不上」
 * 这种谁也说不清的问题。本页只做一件事：把两条序列画出来。
 *
 * **两个面板共用一套几何与一套模板**：折线的坐标计算在 `components/lineChart.ts` 里，
 * 面板本身由 {@link panels} 描述后交给同一个 `v-for` 渲染。
 * 两份「长得一样但各写一遍」的 SVG 迟早会在留白、刻度或退化处理上分叉，
 * 而它们恰恰是必须逐字相同的部分（两个面板上下叠着，差一个像素都看得出来）。
 * 各面板**自己的**东西——标题、系列、概览数字、无障碍描述、空状态文案——仍各写各的
 * （在 {@link assetsPanel} / {@link flowsPanel} 里），那几处本来就该不同。
 *
 * **只画「确实落过库」的日子**：缺日不补零。补零会把「这天还没结算」画成「这天为 0」
 * ——图上是一根掉到底的线，那是错误信息，而不是「这一天没有数据」这一事实的如实呈现。
 * 因此曲线的横轴是**数据点序号**而不是日期序号：两天之间有间隙时，图上不会出现一段平地，
 * 代价是横轴的疏密与真实日期不完全等比——两端与中间标出实际日期作为补偿。
 *
 * 金额一律用 `Intl.NumberFormat` 固定两位小数呈现，与后端的 `decimal(...,2)` 对齐；
 * 颜色只用 `base.css` 的语义令牌（总资产取品牌强调色、净资产取转账蓝、收入取绿、支出取红），
 * 不出现硬编码色值。**支出的线画在正半轴**：接口给的就是正数（「支出 500」就是 500），
 * 图上不替它加一个减号——加号的规矩要说给每个看数的人听，而图例上的「支出」二字已经说清了方向。
 *
 * 数据**可能不是今天的**：记录在结算事件之后才落库，故最新一天通常是「上一个结算日」。
 * 这一点写在副标题里，而不是让用户对着一个不动的数字猜。
 */

import { computed, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import { buildLineChart, pickAxisLabels, VIEW_SIZE } from '@/components/lineChart'
import type { LineChartSeries, LineChartTick } from '@/components/lineChart'
import { useAccountSetsStore } from '@/stores/accountSets'
import { useIncomeExpensesStore } from '@/stores/incomeExpenses'
import type { IncomeExpenseDailyPoint } from '@/stores/incomeExpenses'
import { useTotalAssetsStore } from '@/stores/totalAssets'
import type { TotalAssetDailyPoint } from '@/stores/totalAssets'

const accountSets = useAccountSetsStore()
const totalAssets = useTotalAssetsStore()
const incomeExpenses = useIncomeExpensesStore()

/** 日期文本（`yyyy-MM-dd`）的解析式，用于取「几月几日」。 */
const DAY_PATTERN = /^\d{4}-(\d{2})-(\d{2})$/

/** 金额呈现：固定两位小数，与后端的 `decimal(...,2)` 对齐。 */
const amountFormatter = new Intl.NumberFormat('zh-CN', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/** 当前账套是否已选定；未选定时本页只提示、不请求。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/** 两个面板各自的失败文案；无失败时为空串。**分开存**：一个面板挂了不该把另一个也变成错误。 */
const errors = ref<{ assets: string; flows: string }>({ assets: '', flows: '' })

/** 任一接口在途（刷新按钮的禁用与文案据此判断）。 */
const loading = computed(() => totalAssets.loading || incomeExpenses.loading)

/** 一个概览数字（面板顶部那三个之一）。 */
interface StatView {
  label: string
  /** 已经格式化好的金额文案。 */
  value: string
}

/** 一个面板里图表部分的呈现数据；无数据时整个 `chart` 为 `null`。 */
interface ChartView {
  series: LineChartSeries[]
  /** 纵轴刻度：`y` 为逻辑坐标，`value` 为该处的金额。 */
  ticks: LineChartTick[]
  /** 概览数字（取自最新一个数据点）。 */
  stats: StatView[]
  /** 图例右侧的元信息（最新日期 · 天数 · 币种）。 */
  meta: string
  /** 横轴的三处标注（左端、中间、右端）；重复处为 `null` 以保持三列对齐。 */
  xLabels: (string | null)[]
  /** 图表的无障碍描述：把图上最关键的几个数字读出来，供读屏用户获得等价信息。 */
  ariaLabel: string
}

/** 一个走势面板的完整呈现数据。 */
interface Panel {
  key: 'assets' | 'flows'
  title: string
  error: string
  /** 无数据时提示位上的文案（区分「正在加载」「没有币种」「本月还没结算数据」）。 */
  emptyHint: string
  /** 图表；`null` 表示无可画的数据。 */
  chart: ChartView | null
}

/** 图表周期的文案（`2026年9月`）；月份未知时退回「本月」。 */
const period = computed(() => {
  // 两个面板查的是同一台服务器的同一个月，取哪个都行；先有值的那个先算
  const month = totalAssets.daily?.month ?? incomeExpenses.daily?.month
  if (month === undefined) {
    return '本月'
  }

  const [year, monthPart] = month.split('-')
  return year === undefined || monthPart === undefined
    ? month
    : `${year}年${Number(monthPart)}月`
})

/** 图例右侧的元信息。币种为空串时不留一个孤零零的分隔点。 */
function buildMeta(latestLabel: string, dayCount: number, currencyLabel: string): string {
  return `${latestLabel} · 共 ${dayCount} 天${currencyLabel === '' ? '' : ` · ${currencyLabel}`}`
}

/** 总资产面板：两条线共用一根纵轴（各自一根会让「净资产低于总资产」这个事实在图上消失）。 */
function assetsPanel(): Panel {
  const days = totalAssets.daily?.days ?? []
  const currencyLabel = totalAssets.daily?.currencyCode ?? ''
  const chart = buildLineChart<TotalAssetDailyPoint>(days, [
    // 总资产 = 资金账户合计（不含负债）；净资产 = 总资产 + 负债（负债带符号，故此处是加）
    { key: 'asset', label: '总资产', pick: (day) => day.assetTotal },
    { key: 'net', label: '净资产', pick: (day) => day.netTotal },
  ])
  const latest = days[days.length - 1]

  return {
    key: 'assets',
    title: '总资产与净资产（本月，含公共账户）',
    error: errors.value.assets,
    emptyHint: emptyHint(
      totalAssets.loading,
      totalAssets.daily?.currencyCode,
      '总资产由结算订阅在每个结算日之后按天落库，账套尚未走完一次结算时这里是空的。',
    ),
    chart:
      chart === null || latest === undefined
        ? null
        : {
            series: chart.series,
            ticks: chart.ticks,
            stats: [
              { label: '总资产', value: formatAmount(latest.assetTotal) },
              { label: '净资产', value: formatAmount(latest.netTotal) },
              { label: '负债合计', value: formatAmount(latest.liabilityTotal) },
            ],
            meta: buildMeta(formatDay(latest.date), days.length, currencyLabel),
            xLabels: pickAxisLabels(days.map((day) => day.date)).map(formatDayOrNull),
            ariaLabel:
              `${period.value}总资产与净资产走势图，共 ${days.length} 个数据点。` +
              `最新一天（${formatDay(latest.date)}）总资产 ${formatAmount(latest.assetTotal)}、` +
              `净资产 ${formatAmount(latest.netTotal)}、` +
              `负债 ${formatAmount(latest.liabilityTotal)}` +
              `${currencyLabel === '' ? '' : ` ${currencyLabel}`}。`,
          },
  }
}

/**
 * 收支面板。
 *
 * **支出线与收入线同在正半轴**：接口给的两个金额都是正数（见后端 `IncomeExpenseDailyPoint`），
 * 图上不再叠一层符号——「哪个是花出去的」由图例与颜色说清，而「这个月净赚多少」由净值那个数字回答。
 */
function flowsPanel(): Panel {
  const days = incomeExpenses.daily?.days ?? []
  const currencyLabel = incomeExpenses.daily?.currencyCode ?? ''
  const chart = buildLineChart<IncomeExpenseDailyPoint>(days, [
    { key: 'income', label: '收入', pick: (day) => day.incomeTotal },
    { key: 'expense', label: '支出', pick: (day) => day.expenseTotal },
  ])
  const latest = days[days.length - 1]

  return {
    key: 'flows',
    title: '收入与支出（本月，含公共账户）',
    error: errors.value.flows,
    emptyHint: emptyHint(
      incomeExpenses.loading,
      incomeExpenses.daily?.currencyCode,
      '收入与支出由各自的结算订阅在每个结算日之后按天落库，账套尚未走完一次结算时这里是空的。',
    ),
    chart:
      chart === null || latest === undefined
        ? null
        : {
            series: chart.series,
            ticks: chart.ticks,
            stats: [
              { label: '收入', value: formatAmount(latest.incomeTotal) },
              { label: '支出', value: formatAmount(latest.expenseTotal) },
              { label: '净额', value: formatAmount(latest.netTotal) },
            ],
            meta: buildMeta(formatDay(latest.date), days.length, currencyLabel),
            xLabels: pickAxisLabels(days.map((day) => day.date)).map(formatDayOrNull),
            ariaLabel:
              `${period.value}收入与支出走势图，共 ${days.length} 个数据点。` +
              `最新一天（${formatDay(latest.date)}）收入 ${formatAmount(latest.incomeTotal)}、` +
              `支出 ${formatAmount(latest.expenseTotal)}、` +
              `净额 ${formatAmount(latest.netTotal)}` +
              `${currencyLabel === '' ? '' : ` ${currencyLabel}`}。`,
          },
  }
}

/** 两个面板，按上总资产、下收支的顺序呈现。 */
const panels = computed<Panel[]>(() => [assetsPanel(), flowsPanel()])

/**
 * 面板内提示位上的文案（加载中、没有币种、本月还没数据，三种情况）。
 *
 * @param currencyCode 该面板最近一次结果的币种：`null` 是**后端明确回答「没有可用币种」**，
 *   `undefined` 是**还没查过**。两者必须分开——用 `??` 把 `null` 并到 `undefined` 上，
 *   「一个币种都没有」就会被当成「还没查」，用户看到一句与事实无关的「本月还没有结算数据」。
 */
function emptyHint(
  isLoading: boolean,
  currencyCode: string | null | undefined,
  noDataHint: string,
): string {
  if (isLoading) {
    return '加载中…'
  }

  if (currencyCode === null) {
    return '还没有可用的币种，请先在管理端添加币种后记账。'
  }

  return `本月还没有结算数据：${noDataHint}`
}

/** 格式化金额（固定两位小数、千分位）。 */
function formatAmount(value: number): string {
  return Number.isFinite(value) ? amountFormatter.format(value) : String(value)
}

/** 把 `yyyy-MM-dd` 显示成 `M月D日`；无法解析时原样回显。 */
function formatDay(date: string): string {
  const matched = DAY_PATTERN.exec(date)
  if (matched === null) {
    return date
  }

  return `${Number(matched[1])}月${Number(matched[2])}日`
}

/** 横轴标注用：`null` 仍为 `null`（那是「这个位置不标」而不是一个日期）。 */
function formatDayOrNull(date: string | null): string | null {
  return date === null ? null : formatDay(date)
}

/**
 * 拉取两块当月序列。
 *
 * **不传月份**，由后端取服务器本地的当月：记录的日期是本地日期，
 * 跟着服务器走才不会与落库的日期错位（前端按浏览器时区算一个月，跨时区部署时就会差一天）。
 *
 * 两个请求**并发**发出、**各自**记账自己的失败：串行会让下面那个面板白等上面那个的网络往返，
 * 而用 `Promise.all` 的「一票否决」语义则会让总资产接口的一次超时把收支面板也变成一片空白。
 */
async function load(): Promise<void> {
  if (!hasAccountSet.value) {
    return
  }

  errors.value = { assets: '', flows: '' }

  await Promise.all([
    totalAssets.loadDaily().catch((error: unknown) => {
      errors.value.assets = error instanceof ApiError ? error.message : '加载总资产失败'
    }),
    incomeExpenses.loadDaily().catch((error: unknown) => {
      errors.value.flows = error instanceof ApiError ? error.message : '加载收支失败'
    }),
  ])
}

// 账套切换后必须重来一遍：两块数据都按账套隔离，且**记录按用户分行**——
// 同一本账套里换个人看到的个人账户不同，故结果是「这个账套里、我这个人的」那一份。
// 未选择账套时清空，避免退出登录后仍残留可见数据。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    totalAssets.clear()
    incomeExpenses.clear()
    errors.value = { assets: '', flows: '' }

    if (currentId === null) {
      return
    }

    await load()
  },
)

onMounted(() => {
  if (hasAccountSet.value) {
    void load()
  }
})
</script>

<template>
  <main class="home">
    <header class="head">
      <div>
        <h1 class="title">概览</h1>
        <p class="subtitle">
          {{ period }}的走势概览（含公共账户）。数据由结算订阅在每个结算日之后按天落库，
          故最新一天通常是上一个结算日，而不是今天。
        </p>
      </div>
      <div class="head-actions">
        <!-- 本页只有「刷新」一个动作：数据由后端订阅落库，前端没有可触发的手段 -->
        <button type="button" class="ghost" :disabled="loading || !hasAccountSet" @click="load">
          {{ loading ? '刷新中…' : '刷新' }}
        </button>
      </div>
    </header>

    <p v-if="!hasAccountSet" class="hint">
      请先在右上角选择账套，首页展示的是当前账套里的总资产与收支。
    </p>

    <template v-else>
      <section v-for="panel in panels" :key="panel.key" class="panel">
        <h2 class="panel-title">{{ panel.title }}</h2>

        <p v-if="panel.error !== ''" class="error">{{ panel.error }}</p>
        <p v-else-if="panel.chart === null" class="hint">{{ panel.emptyHint }}</p>

        <template v-else>
          <div class="stats">
            <div v-for="stat in panel.chart.stats" :key="stat.label" class="stat">
              <span class="stat-label">{{ stat.label }}</span>
              <strong class="stat-value">{{ stat.value }}</strong>
            </div>
          </div>

          <div class="legend">
            <span v-for="item in panel.chart.series" :key="item.key" class="legend-item">
              <i class="swatch" :class="`swatch-${item.key}`" />
              {{ item.label }}
            </span>
            <span class="legend-meta">{{ panel.chart.meta }}</span>
          </div>

          <div class="chart">
            <div class="y-axis">
              <span v-for="tick in panel.chart.ticks" :key="tick.y">
                {{ formatAmount(tick.value) }}
              </span>
            </div>

            <svg
              class="plot"
              :viewBox="`0 0 ${VIEW_SIZE} ${VIEW_SIZE}`"
              preserveAspectRatio="none"
              role="img"
              :aria-label="panel.chart.ariaLabel"
            >
              <line
                v-for="tick in panel.chart.ticks"
                :key="tick.y"
                class="grid"
                x1="0"
                :y1="tick.y"
                :x2="VIEW_SIZE"
                :y2="tick.y"
              />
              <path
                v-for="item in panel.chart.series"
                :key="item.key"
                class="line"
                :class="`line-${item.key}`"
                :d="item.path"
                vector-effect="non-scaling-stroke"
              />
            </svg>

            <div class="x-axis">
              <span v-for="(label, index) in panel.chart.xLabels" :key="index">
                {{ label ?? '' }}
              </span>
            </div>
          </div>
        </template>
      </section>
    </template>
  </main>
</template>

<style scoped>
.home {
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

/* 次要按钮：与账户页 / 明细页的「重置」同源（描边主色、无底色，hover 才铺淡底） */
.ghost {
  flex: none;
  padding: 0.3rem 0.7rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: none;
  color: var(--color-accent-strong);
  font-family: inherit;
  font-size: 12.5px;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s;
}

@media (hover: hover) {
  .ghost:not(:disabled):hover {
    background-color: var(--color-accent-soft);
  }
}

.ghost:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

/* 图表面板：与明细页的筛选区同一套描边 + 卡片阴影 */
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

/* 面板标题：用 h2 而不是「加大加粗的 p」，读屏用户据此在页内跳转 */
.panel-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-heading);
  opacity: 0.85;
}

/*
 * 面板内的状态文案（加载中 / 暂无数据 / 失败）：
 * 卡片自己已经有描边与底色，这两行再各套一层框会变成「框里套框」，
 * 故在此把外边距与底色让掉，只保留文字本身。
 */
.panel > .hint,
.panel > .error {
  padding: 0.15rem 0;
  border: none;
  border-radius: 0;
  background: none;
}

/* 三个概览数字：等宽分栏，窄屏自动折行 */
.stats {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem 2rem;
}

.stat {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
}

.stat-label {
  font-size: 12px;
  opacity: 0.7;
}

.stat-value {
  font-size: 19px;
  font-weight: 600;
  color: var(--color-heading);
  /* 数字等宽，切换账套时数字跳变不会带着整块布局左右晃 */
  font-variant-numeric: tabular-nums;
}

.legend {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.4rem 1rem;
  font-size: 12px;
}

.legend-item {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  opacity: 0.85;
}

.legend-meta {
  margin-left: auto;
  opacity: 0.65;
}

.swatch {
  width: 10px;
  height: 10px;
  border-radius: 2px;
}

/*
 * 系列配色只用语义令牌，四色两两可辨：
 * 总资产取品牌强调色、净资产取转账蓝、收入取绿、支出取红——
 * 后两者的语义令牌本来就叫「收入 / 支出」（收款页、记账页已在用），此处沿用同一个词、不另配色。
 */
.swatch-asset {
  background-color: var(--color-accent);
}

.swatch-net {
  background-color: var(--color-transfer);
}

.swatch-income {
  background-color: var(--color-income);
}

.swatch-expense {
  background-color: var(--color-expense);
}

/*
 * 绘图区：纵轴标签单独一列（HTML 文本，不随 SVG 拉伸变形），
 * 绘图区与横轴标签同处右列的两行，故横轴标签与曲线左右对齐。
 */
.chart {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 0.3rem 0.6rem;
}

.head-actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 0.5rem;
}

.y-axis {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  align-items: flex-end;
  font-size: 11px;
  line-height: 1;
  white-space: nowrap;
  opacity: 0.65;
  font-variant-numeric: tabular-nums;
}

.plot {
  grid-column: 2;
  display: block;
  width: 100%;
  height: 240px;
  overflow: visible;
}

.grid {
  stroke: var(--color-border);
  stroke-width: 1;
  vector-effect: non-scaling-stroke;
}

.line {
  fill: none;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.line-asset {
  stroke: var(--color-accent);
}

.line-net {
  stroke: var(--color-transfer);
}

.line-income {
  stroke: var(--color-income);
}

.line-expense {
  stroke: var(--color-expense);
}

.x-axis {
  grid-column: 2;
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  opacity: 0.65;
}

/* 窄屏：图表降高，避免一屏只看得见曲线看不见别的 */
@media (max-width: 1023px) {
  .plot {
    height: 180px;
  }
}
</style>
