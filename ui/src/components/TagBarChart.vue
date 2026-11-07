<script setup lang="ts">
/**
 * 标签构成竖向条形图（Apache ECharts 6 封装）：把「标签 → 金额」画成一张按金额降序的条形图——
 * **标签名称在横轴（X），金额在纵轴（Y）**——并在图下列出每个标签的名称、占比与金额。
 *
 * *日报表 / 月报表的「收入标签构成 / 支出标签构成」用的是同一张图*——同一套排序、同一个空状态、
 * 同一个列表结构，差异只在标题、数据、空态文案、`nullLabel` 与条形颜色（收入 / 支出语义色）。
 * option 的构建、排序与百分比计算在 `./tagBar.ts`（纯函数，不含 Vue），本文件只负责
 * 组件侧的三件事：**按需注册 ECharts 模块**、**跟随系统配色重绘**、**无障碍标签**。
 *
 * **为什么是条形图而不是饼图**：标签是数量不定的多值维度，且本页要求**全部显示**。
 * 饼图在标签很多时扇区会细到无法分辨，条形图可以逐个排开。竖向条形图的代价是横轴文字
 * 空间有限：标签名过长会被截断、标签多时旋转 45°/90°——完整名称因此由 tooltip 补齐，
 * 图形只承担「谁多谁少」的比较。条形图因此不做「前 5 + 其他」的折叠。
 *
 * **单系列**：一张图只表达收入或支出，颜色由 `colorToken` 指定为对应语义色；
 * 不注册 `LegendComponent`——标题已经说明这张图是什么，图例只会重复一遍。
 *
 * **图下不再有可见列表**：报表页里收入与支出各占一整行，横轴空间足够，逐项的完整名称、
 * 占比与金额都由图与悬停气泡给出；图下若再排一份同样的文字列表只是重复。
 * 但 canvas 内容读屏拿不到、tooltip 也读不到，故仍保留一份**视觉隐藏**的等价列表
 * （`.bar-sr`）供读屏逐项读取——它不占版面，也不与图形争视觉焦点。
 *
 * **配色跟随系统**：canvas 拿不到 `var(--color-income)`，色值必须由 `readChartTheme()`
 * 从计算样式里读出来；系统在亮/暗之间切换时监听 `prefers-color-scheme` 并重建 option
 * （与 `CategoryPieChart.vue` / `TrendChart.vue` 同一写法）。
 *
 * **无障碍**：`role="img"` + 一句话 `aria-label` 挂在 canvas 容器上，说明标签数与合计；
 * 每一项的细节由紧随其后的视觉隐藏列表承担。
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

/**
 * 画布高度（px）。
 *
 * 竖向条形图里标签沿**横轴**排开，条数只影响每根的宽度、不影响行数，故高度恒定；
 * `grid.containLabel` 会先给旋转后的横轴文字留出空间，其余才是绘图区。
 * 报表页里两张图各占一整行，宽度足够，故这一高度无需再随容器列宽调整。
 */
const CHART_HEIGHT = 260

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

/** 排序、过滤、算占比都在这里一次完成；不折叠，全部标签各一根竖条。 */
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

/** 读屏描述：标签数与合计；逐项数据在紧随其后的视觉隐藏列表里。 */
const description = computed(() =>
  buildTagBarLabel(
    props.title,
    props.period,
    bars.value.length,
    total.value,
    props.currencyCode,
  ),
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
           这一句给出概览，逐项数值由紧随其后的视觉隐藏列表承担 -->
      <div
        class="bar-canvas"
        role="img"
        :aria-label="description"
        :style="{ height: `${CHART_HEIGHT}px` }"
      >
        <VChart class="bar-chart" :option="option" autoresize />
      </div>

      <!-- 仅供读屏：图形与 tooltip 都拿不到逐项文字，这份视觉隐藏的列表补上
           名称、占比与金额；不占版面，不重复显示在用户眼前 -->
      <ul class="bar-sr">
        <li v-for="bar in bars" :key="bar.key">
          {{ bar.name }}，{{ formatPercent(bar.percent) }}，{{ formatAmount(bar.amount) }}
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

/* 画布高度由组件写成内联样式（恒定）：标签沿横轴排开，全部显示、不滚动、不截断数据 */
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

/*
 * 标准 visually-hidden：把逐项文字移出视觉流，但仍留在无障碍树里。
 * 不能用 display: none / visibility: hidden——那会连读屏一起藏掉。
 */
.bar-sr {
  position: absolute;
  width: 1px;
  height: 1px;
  margin: -1px;
  padding: 0;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
  border: 0;
}
</style>
