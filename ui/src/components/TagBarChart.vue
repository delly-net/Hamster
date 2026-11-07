<script setup lang="ts">
/**
 * 标签构成横向条形图（Apache ECharts 6 封装）：把「标签 → 金额」画成一张按金额降序的条形图，
 * 并在图下列出每个标签的名称、占比与金额。
 *
 * *日报表 / 月报表的「收入标签构成 / 支出标签构成」用的是同一张图*——同一套排序、同一个空状态、
 * 同一个列表结构，差异只在标题、数据、空态文案、`nullLabel` 与条形颜色（收入 / 支出语义色）。
 * option 的构建、排序与百分比计算在 `./tagBar.ts`（纯函数，不含 Vue），本文件只负责
 * 组件侧的三件事：**按需注册 ECharts 模块**、**跟随系统配色重绘**、**无障碍标签**。
 *
 * **为什么是条形图而不是饼图**：标签是数量不定的多值维度，且本页要求**全部显示**。
 * 饼图在标签很多时扇区会细到无法分辨，而横向条形图的纵轴可以继续往下排；
 * 标签名也比扇区上的角度标签更容易读。条形图因此不做「前 5 + 其他」的折叠。
 *
 * **单系列**：一张图只表达收入或支出，颜色由 `colorToken` 指定为对应语义色；
 * 不注册 `LegendComponent`——标题已经说明这张图是什么，图例只会重复一遍。
 *
 * **列表兼表格视图**：纵轴名称会被截断，图下方逐项列出完整名称、占比与金额，
 * 同时充当读屏用户的等价文本与色觉障碍用户的读数通道。
 *
 * **配色跟随系统**：canvas 拿不到 `var(--color-income)`，色值必须由 `readChartTheme()`
 * 从计算样式里读出来；系统在亮/暗之间切换时监听 `prefers-color-scheme` 并重建 option
 * （与 `CategoryPieChart.vue` / `TrendChart.vue` 同一写法）。
 *
 * **无障碍**：`role="img"` + 一句话 `aria-label` 挂在 canvas 容器上，说明标签数与合计；
 * 每一项的细节由下方可见列表承担。
 */

import { computed, onMounted, onUnmounted, ref } from 'vue'
import VChart from 'vue-echarts'
import { BarChart } from 'echarts/charts'
import { GridComponent, TooltipComponent } from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import type { EntryCategoryTotal } from '@/stores/categoryTotals'
import { UNCLASSIFIED_LABEL, formatPercent } from './categoryPie'
import { buildTagBarItems, buildTagBarLabel, buildTagBarOption } from './tagBar'
import type { TagBarOption } from './tagBar'
import { formatAmount } from './entryFormat'
import { readChartTheme } from './trendChart'
import type { ChartTheme, ChartToken } from './trendChart'

use([CanvasRenderer, BarChart, GridComponent, TooltipComponent])

/** 每条横条的基准高度（px，含行间隙）；标签越多画布越高，全部标签都看得见。 */
const ROW_HEIGHT = 30
/** 画布最小高度（px）：只有一两个标签时不至于压成一条扁图。 */
const MIN_CHART_HEIGHT = 180
/** 横轴刻度文字占用的高度（px）。 */
const AXIS_HEIGHT = 24

const props = withDefaults(
  defineProps<{
    /** 图名（「收入标签构成」/「支出标签构成」），同时用作系列名。 */
    title: string
    /** 本侧的「标签 → 金额」；为空时呈现 `emptyText`。 */
    items: readonly EntryCategoryTotal[]
    /** 金额所属币种；`null` 表示没有可用币种（调用方通常已先拦掉这种情形）。 */
    currencyCode: string | null
    /** 周期文案（如「2026年10月」），用于合计行与读屏描述。 */
    period: string
    /** 本侧没有数据时的提示文案。 */
    emptyText: string
    /**
     * 「空值分项」的呈现文案（`categoryId` 为 `null` 的那一项叫法）。
     *
     * 默认「未分类」；标签维度由调用方传「未打标签」。
     */
    nullLabel?: string
    /**
     * 条形颜色令牌名。
     *
     * 收入侧传 `--color-income`、支出侧传 `--color-expense`；默认主强调色只是兜底，
     * 调用方应显式给出，避免两张图同色。
     */
    colorToken?: ChartToken
  }>(),
  { nullLabel: UNCLASSIFIED_LABEL, colorToken: '--color-accent' },
)

/**
 * 当前配色下的绘图主题。
 *
 * 放成 `ref` 而不是 `computed`：色值来自计算样式，Vue 追踪不到它的变化，
 * 只能由系统配色的监听主动重读后写回（见下）。
 */
const theme = ref<ChartTheme>(readChartTheme())

/** 排序、过滤、算占比都在这里一次完成；不折叠，全部标签各一根横条。 */
const bars = computed(() => buildTagBarItems(props.items, props.nullLabel))

/** 本侧合计（标签金额之和，多标签重复计入）：用于标题旁的数字与宽度为 0 时的兜底判断。 */
const total = computed(() => bars.value.reduce((sum, bar) => sum + bar.amount, 0))

const option = computed<TagBarOption>(() =>
  buildTagBarOption({
    bars: bars.value,
    theme: theme.value,
    title: props.title,
    colorToken: props.colorToken,
  }),
)

/** 币种后缀（合计行与 tooltip 共用同一写法）。 */
const currencyLabel = computed(() =>
  props.currencyCode === null || props.currencyCode === '' ? '' : ` ${props.currencyCode}`,
)

/** 读屏描述：标签数与合计；逐项数据在下方列表里。 */
const description = computed(() =>
  buildTagBarLabel(
    props.title,
    props.period,
    bars.value.length,
    total.value,
    props.currencyCode,
  ),
)

/** 画布高度随标签数增长：每条横条一行，全部标签都排得下。 */
const chartHeight = computed(() =>
  Math.max(MIN_CHART_HEIGHT, bars.value.length * ROW_HEIGHT + AXIS_HEIGHT),
)

/** 系统配色查询对象；仅在挂载期间存在，故可能为 `null`。 */
let colorScheme: MediaQueryList | null = null

/** 亮暗切换后重读令牌：`readChartTheme()` 读的就是当前生效的那一套。 */
function syncTheme(): void {
  theme.value = readChartTheme()
}

onMounted(() => {
  colorScheme = window.matchMedia('(prefers-color-scheme: dark)')
  colorScheme.addEventListener('change', syncTheme)
})

onUnmounted(() => {
  colorScheme?.removeEventListener('change', syncTheme)
  colorScheme = null
})
</script>

<template>
  <figure class="tag-bar">
    <figcaption class="bar-head">
      <h3 class="bar-title">{{ title }}</h3>
      <p class="bar-total">合计 {{ formatAmount(total) }}{{ currencyLabel }}</p>
    </figcaption>

    <p v-if="bars.length === 0" class="bar-empty">{{ emptyText }}</p>

    <template v-else>
      <!-- role="img" + aria-label 挂在 canvas 容器上：canvas 里的内容读屏拿不到，
           这一句给出概览，逐项数值由下方列表承担 -->
      <div
        class="bar-canvas"
        role="img"
        :aria-label="description"
        :style="{ height: `${chartHeight}px` }"
      >
        <VChart class="bar-chart" :option="option" autoresize />
      </div>

      <!-- 图例兼表格视图：名称、占比、金额三列都由文字给出，颜色只表达收入 / 支出 -->
      <ul class="bar-legend">
        <li v-for="bar in bars" :key="bar.key" class="legend-row">
          <span class="legend-name">{{ bar.name }}</span>
          <span class="legend-percent">{{ formatPercent(bar.percent) }}</span>
          <span class="legend-amount">{{ formatAmount(bar.amount) }}</span>
        </li>
      </ul>
    </template>
  </figure>
</template>

<style scoped>
.tag-bar {
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
  min-width: 0;
}

/* 标题行：左侧图名、右侧合计。合计用次要文字，不抢图名的位置 */
.bar-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 0.5rem;
}

.bar-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-heading);
  opacity: 0.88;
}

.bar-total {
  flex: none;
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  opacity: 0.7;
}

/* 画布高度由组件按条数写成内联样式：标签全部显示，不滚动、不截断 */
.bar-canvas {
  width: 100%;
}

/* vue-echarts 的根元素默认就是 block + 100%，此处显式写一遍以免样式表被裁剪时塌成 0 高 */
.bar-chart {
  width: 100%;
  height: 100%;
}

.bar-empty {
  padding: 0.5rem 0;
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.8;
}

.bar-legend {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

/* 三列：名称 / 占比 / 金额。名称可截断，数字列不换行，窄列下也保持纵向对齐 */
.legend-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  align-items: center;
  gap: 0.4rem;
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.legend-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  opacity: 0.85;
}

.legend-percent {
  opacity: 0.7;
}

.legend-amount {
  text-align: right;
  opacity: 0.85;
}
</style>
