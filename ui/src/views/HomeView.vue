<script setup lang="ts">
/**
 * `/` 首页：当前账套本月的总资产走势。
 *
 * 数据来自后端落库的**按天总资产记录**（总资产结算订阅在结算事件后逐日重算），
 * 本页**不做任何金额汇总**——「哪些账户算资产、哪些算负债、个人与公共如何相加、负债的符号」
 * 全部由后端一处定义（见 `ITotalAssetSettlementService`），前端再算一遍就会出现
 * 「首页的曲线与账户页的余额对不上」这种谁也说不清的问题。本页只做一件事：把这条序列画出来。
 *
 * **曲线画在自带的 SVG 里，不引图表库**：本项目的依赖只有 vue / vue-router / pinia，
 * 为一条折线引入一个图表库（连同它的主题、响应式与打包体积）不划算；
 * 而折线本身用 `<polyline>` 的两个坐标点集就能表达清楚（见 {@link chart}）。
 * 坐标系的宽高是**逻辑值** 0~100（`viewBox`），实际像素由 CSS 决定，
 * 故缩放由 `preserveAspectRatio="none"` 完成——代价是线条会被横向拉粗，
 * 用 `vector-effect="non-scaling-stroke"` 把描边宽度固定在设备像素上抵消它。
 * **轴标签一律画在 SVG 之外**（HTML 元素）：SVG 被拉伸后，里面的文字会随之变形，
 * 而 HTML 文本不受影响、还能继承全站的字体与配色令牌。
 *
 * **只画「确实落过库」的日子**：缺日不补零。补零会把「这天还没结算」画成「这天资产为 0」
 * ——图上是一根掉到底的线，那是错误信息，而不是「这一天没有数据」这一事实的如实呈现。
 * 因此曲线的横轴是**数据点序号**而不是日期序号：两天之间有间隙时，图上不会出现一段平地，
 * 代价是横轴的疏密与真实日期不完全等比——两端与中间标出实际日期作为补偿。
 *
 * 金额一律用 `Intl.NumberFormat` 固定两位小数呈现，与后端的 `decimal(...,2)` 对齐；
 * 颜色只用 `base.css` 的语义令牌（总资产取品牌强调色、净资产取转账蓝），不出现硬编码色值。
 *
 * 数据**可能不是今天的**：记录在结算事件之后才落库，故最新一天通常是「上一个结算日」。
 * 这一点写在副标题里，而不是让用户对着一个不动的数字猜。
 */

import { computed, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import { useAccountSetsStore } from '@/stores/accountSets'
import { useTotalAssetsStore } from '@/stores/totalAssets'
import type { TotalAssetDailyPoint } from '@/stores/totalAssets'

const accountSets = useAccountSetsStore()
const totalAssets = useTotalAssetsStore()

/** 图表的逻辑坐标系边长：`viewBox` 用 0~100 的方形，实际宽高交给 CSS。 */
const VIEW_SIZE = 100

/** 纵轴上下的留白比例：折线贴着画布边缘不好看，也容易被描边裁掉。 */
const PADDING_RATIO = 0.12

/** 网格线（也就是纵轴刻度）的纵向位置比例：上界、中值、下界。 */
const TICK_RATIOS = [0, 0.5, 1] as const

/** 日期文本（`yyyy-MM-dd`）的解析式，用于取「几月几日」。 */
const DAY_PATTERN = /^\d{4}-(\d{2})-(\d{2})$/

/** 金额呈现：固定两位小数，与后端的 `decimal(...,2)` 对齐。 */
const amountFormatter = new Intl.NumberFormat('zh-CN', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/** 当前账套是否已选定；未选定时本页只提示、不请求。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/** 本次加载的失败文案；无失败时为空串。 */
const errorMessage = ref('')

/** 一条折线的呈现信息（数据与外观都在这里，模板只负责画）。 */
interface ChartSeries {
  /** 系列标识，同时用于图例色块与折线的样式类后缀。 */
  key: 'asset' | 'net'
  /** 图例文案。 */
  label: string
  /** `path` 的 `d` 属性（逻辑坐标系下的折线）。 */
  path: string
}

/** 图表的全部呈现数据；无数据时为 `null`。 */
interface Chart {
  series: ChartSeries[]
  /** 纵轴刻度：`y` 为逻辑坐标，`value` 为该处的金额。 */
  ticks: { y: number; value: number }[]
  /** 最新一个数据点（用于概览数字）。 */
  latest: TotalAssetDailyPoint
  /** 最新数据点的日期文案（`M月D日`）。 */
  latestLabel: string
}

/**
 * 由当前序列算出图表几何。
 *
 * 纵轴范围取两条线的**全部取值**的极值再各留一点余白：两条线共用一根纵轴才可比，
 * 各用各的轴会让「净资产低于总资产」这个事实在图上消失。
 * 所有取值相同时（如整月只有一天）给一个对称的人工范围，避免除零。
 */
const chart = computed<Chart | null>(() => {
  const days = totalAssets.daily?.days ?? []
  const first = days[0]
  const last = days[days.length - 1]
  if (first === undefined || last === undefined) {
    return null
  }

  const values = days.flatMap((day) => day.netTotal)
  let min = Math.min(...values)
  let max = Math.max(...values)

  if (min === max) {
    const span = max === 0 ? 1 : Math.abs(max) * PADDING_RATIO
    min -= span
    max += span
  } else {
    const padding = (max - min) * PADDING_RATIO
    min -= padding
    max += padding
  }

  // 单点居中：只有一天时把点放在画布中间，而不是最左边
  const toX = (index: number) =>
    days.length === 1 ? VIEW_SIZE / 2 : (index / (days.length - 1)) * VIEW_SIZE
  const toY = (value: number) => ((max - value) / (max - min)) * VIEW_SIZE

  /** 把一条取值序列折成 `path` 的 `d`；单点时补一小段横线（一个坐标画不出折线）。 */
  const buildPath = (pick: (day: TotalAssetDailyPoint) => number): string => {
    const coords: [number, number][] = days.map((day, index) => [toX(index), toY(pick(day))])
    const head = coords[0]
    if (head === undefined) {
      return ''
    }

    if (coords.length === 1) {
      const [x, y] = head
      return `M ${x.toFixed(2)} ${y.toFixed(2)} L ${(x + 0.5).toFixed(2)} ${y.toFixed(2)}`
    }

    return coords
      .map(([x, y], index) => `${index === 0 ? 'M' : 'L'} ${x.toFixed(2)} ${y.toFixed(2)}`)
      .join(' ')
  }

  return {
    series: [
      // 总资产 = 资金账户合计（不含负债）；净资产 = 总资产 + 负债（负债带符号，故此处是加）
      { key: 'asset', label: '总资产', path: buildPath((day) => day.assetTotal) },
      { key: 'net', label: '净资产', path: buildPath((day) => day.netTotal) },
    ],
    ticks: TICK_RATIOS.map((ratio) => ({
      y: ratio * VIEW_SIZE,
      value: max - ratio * (max - min),
    })),
    latest: last,
    latestLabel: formatDay(last.date),
  }
})

/** 横轴的三处标注（左端、中间、右端）；重复的日期只标一次，空位留空以保持三列对齐。 */
const xLabels = computed<(string | null)[]>(() => {
  const days = totalAssets.daily?.days ?? []
  const first = days[0]
  const last = days[days.length - 1]
  if (first === undefined || last === undefined) {
    return []
  }

  const middle = days[Math.floor((days.length - 1) / 2)] ?? first
  const dates = [first.date, middle.date, last.date]

  return dates.map((date, index) => (dates.indexOf(date) === index ? formatDay(date) : null))
})

/** 图表周期的文案（`2026年9月`）；月份未知时退回「本月」。 */
const period = computed(() => {
  const month = totalAssets.daily?.month
  if (month === undefined) {
    return '本月'
  }

  const [year, monthPart] = month.split('-')
  return year === undefined || monthPart === undefined
    ? month
    : `${year}年${Number(monthPart)}月`
})

/** 图表所在币种的文案；无币种时为空串。 */
const currencyLabel = computed(() => totalAssets.daily?.currencyCode ?? '')

/** 图表的无障碍描述：把图上最关键的三个数字读出来，供读屏用户获得等价信息。 */
const chartLabel = computed(() => {
  const current = chart.value
  if (current === null) {
    return '总资产走势图'
  }

  return (
    `${period.value}总资产与净资产走势图，共 ${totalAssets.daily?.days.length ?? 0} 个数据点。` +
    `最新一天（${current.latestLabel}）总资产 ${formatAmount(current.latest.assetTotal)}、` +
    `净资产 ${formatAmount(current.latest.netTotal)}、` +
    `负债 ${formatAmount(current.latest.liabilityTotal)}${currencyLabel.value === '' ? '' : ` ${currencyLabel.value}`}。`
  )
})

/** 无图表时提示位上的文案：区分「正在加载」「没有币种」「本月还没结算数据」三种情况。 */
const emptyHint = computed(() => {
  if (totalAssets.loading) {
    return '加载中…'
  }

  if (totalAssets.daily?.currencyCode === null) {
    return '还没有可用的币种，请先在管理端添加币种后记账。'
  }

  return '本月还没有结算数据：总资产由结算订阅在每个结算日之后按天落库，账套尚未走完一次结算时这里是空的。'
})

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

/**
 * 拉取当月的总资产序列。
 *
 * **不传月份**，由后端取服务器本地的当月：记录的日期是本地日期，
 * 跟着服务器走才不会与落库的日期错位（前端按浏览器时区算一个月，跨时区部署时就会差一天）。
 */
async function load(): Promise<void> {
  if (!hasAccountSet.value) {
    return
  }

  errorMessage.value = ''
  try {
    await totalAssets.loadDaily()
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载总资产失败'
  }
}

// 账套切换后必须重来一遍：总资产按账套隔离，且**记录按用户分行**——
// 同一本账套里换个人看到的个人账户不同，故结果是「这个账套里、我这个人的」那一份。
// 未选择账套时清空，避免退出登录后仍残留可见数据。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    totalAssets.clear()
    errorMessage.value = ''

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
        <h1 class="title">总资产</h1>
        <p class="subtitle">
          {{ period }}的资产与净资产走势（含公共账户）。数据由结算订阅在每个结算日之后按天落库，
          故最新一天通常是上一个结算日，而不是今天。
        </p>
      </div>
      <div class="head-actions">
        <!-- 本页只有「刷新」一个动作：数据由后端订阅落库，前端没有可触发的手段 -->
        <button
          type="button"
          class="ghost"
          :disabled="totalAssets.loading || !hasAccountSet"
          @click="load"
        >
          {{ totalAssets.loading ? '刷新中…' : '刷新' }}
        </button>
      </div>
    </header>

    <p v-if="!hasAccountSet" class="hint">
      请先在右上角选择账套，首页展示的是当前账套里的总资产。
    </p>

    <template v-else>
      <p v-if="errorMessage !== ''" class="error">{{ errorMessage }}</p>
      <p v-else-if="chart === null" class="hint">{{ emptyHint }}</p>

      <section v-else class="panel">
        <div class="stats">
          <div class="stat">
            <span class="stat-label">总资产</span>
            <strong class="stat-value">{{ formatAmount(chart.latest.assetTotal) }}</strong>
          </div>
          <div class="stat">
            <span class="stat-label">净资产</span>
            <strong class="stat-value">{{ formatAmount(chart.latest.netTotal) }}</strong>
          </div>
          <div class="stat">
            <span class="stat-label">负债合计</span>
            <strong class="stat-value">{{ formatAmount(chart.latest.liabilityTotal) }}</strong>
          </div>
        </div>

        <div class="legend">
          <span v-for="item in chart.series" :key="item.key" class="legend-item">
            <i class="swatch" :class="`swatch-${item.key}`" />
            {{ item.label }}
          </span>
          <span class="legend-meta">
            {{ chart.latestLabel }} · 共 {{ totalAssets.daily?.days.length ?? 0 }} 天 ·
            {{ currencyLabel }}
          </span>
        </div>

        <div class="chart">
          <div class="y-axis">
            <span v-for="tick in chart.ticks" :key="tick.y">{{ formatAmount(tick.value) }}</span>
          </div>

          <svg
            class="plot"
            :viewBox="`0 0 ${VIEW_SIZE} ${VIEW_SIZE}`"
            preserveAspectRatio="none"
            role="img"
            :aria-label="chartLabel"
          >
            <line
              v-for="tick in chart.ticks"
              :key="tick.y"
              class="grid"
              x1="0"
              :y1="tick.y"
              :x2="VIEW_SIZE"
              :y2="tick.y"
            />
            <path
              v-for="item in chart.series"
              :key="item.key"
              class="line"
              :class="`line-${item.key}`"
              :d="item.path"
              vector-effect="non-scaling-stroke"
            />
          </svg>

          <div class="x-axis">
            <span v-for="(label, index) in xLabels" :key="index">{{ label ?? '' }}</span>
          </div>
        </div>
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

/* 系列配色只用语义令牌：总资产取品牌强调色，净资产取转账蓝（与转账语义无冲突，是同一套色板里最易分辨的一对） */
.swatch-asset {
  background-color: var(--color-accent);
}

.swatch-net {
  background-color: var(--color-transfer);
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
