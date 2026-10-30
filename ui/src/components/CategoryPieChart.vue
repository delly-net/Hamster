<script setup lang="ts">
/**
 * 分类占比饼图（Apache ECharts 6 封装）：把「分类 → 金额」画成一张饼图，
 * 并在图下列出每个扇区的名称、占比与金额。
 *
 * 首页的收入构成与支出构成用的是同一张图——同一套折叠规则、同一套配色、同一个空状态，
 * 差异只在标题、数据与空态文案，故收在这一处。option 的构建、折叠与百分比计算在
 * `./categoryPie.ts`（纯函数，不含 Vue），本文件只负责组件侧的三件事：
 * **按需注册 ECharts 模块**、**跟随系统配色重绘**、**无障碍标签**。
 *
 * **图例由 DOM 渲染，不用 ECharts 的 `LegendComponent`**：图例要显示名称、占比与金额三列，
 * 组件内置图例表达不了；而且它同时充当这张图的「表格视图」——颜色之外还有文字可读，
 * 色觉障碍用户不必分辨扇区色块。扇区上因此不画标签与指引线，避免窄面板里文字互相压叠。
 *
 * **配色跟随系统**：canvas 拿不到 `var(--color-chart-1)`，色值必须由 `readChartTheme()`
 * 从计算样式里读出来。系统在亮/暗之间切换时令牌值会变，但 canvas 不会自己知道，
 * 故监听 `prefers-color-scheme` 并重读、重建 option（与 `TrendChart.vue` 同一写法）。
 *
 * **无障碍**：`role="img"` + 一句话 `aria-label` 挂在 canvas 容器上，说明扇区数与合计；
 * 每一项的细节由下方可见列表承担。色块 `aria-hidden`——它不携带列表文字之外的信息。
 */

import { computed, onMounted, onUnmounted, ref } from 'vue'
import VChart from 'vue-echarts'
import { PieChart } from 'echarts/charts'
import { TooltipComponent } from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import type { EntryCategoryTotal } from '@/stores/categoryTotals'
import {
  UNCLASSIFIED_LABEL,
  buildCategoryPieLabel,
  buildCategoryPieOption,
  buildCategorySlices,
  formatPercent,
} from './categoryPie'
import type { CategoryPieOption } from './categoryPie'
import { formatAmount } from './entryFormat'
import { readChartTheme } from './trendChart'
import type { ChartTheme } from './trendChart'

use([CanvasRenderer, PieChart, TooltipComponent])

const props = withDefaults(
  defineProps<{
    /** 图名（「收入构成」/「支出构成」），同时用作系列名。 */
    title: string
    /** 本侧的「分类 → 金额」；为空时呈现 `emptyText`。 */
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
     * 默认「未分类」。**标签维度传「未打标签」**：数据形状与折叠规则完全相同，
     * 差别只在 `null` 表示的是「没有分类」还是「没有标签」（见 `categoryPie.ts`）。
     */
    nullLabel?: string
  }>(),
  { nullLabel: UNCLASSIFIED_LABEL },
)

/**
 * 当前配色下的绘图主题。
 *
 * 放成 `ref` 而不是 `computed`：色值来自计算样式，Vue 追踪不到它的变化，
 * 只能由系统配色的监听主动重读后写回（见下）。
 */
const theme = ref<ChartTheme>(readChartTheme())

/** 折叠、排序、算占比与颜色都在这里一次完成。 */
const slices = computed(() => buildCategorySlices(props.items, props.nullLabel))

/** 本侧合计：用于标题旁的数字与宽度为 0 时的兜底判断。 */
const total = computed(() => slices.value.reduce((sum, slice) => sum + slice.amount, 0))

const option = computed<CategoryPieOption>(() =>
  buildCategoryPieOption({ slices: slices.value, theme: theme.value, title: props.title }),
)

/** 币种后缀（合计行与 tooltip 共用同一写法）。 */
const currencyLabel = computed(() =>
  props.currencyCode === null || props.currencyCode === '' ? '' : ` ${props.currencyCode}`,
)

/** 读屏描述：扇区数与合计；逐项数据在下方列表里。 */
const description = computed(() =>
  buildCategoryPieLabel(
    props.title,
    props.period,
    slices.value.length,
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
  <figure class="category-pie">
    <figcaption class="pie-head">
      <h3 class="pie-title">{{ title }}</h3>
      <p class="pie-total">合计 {{ formatAmount(total) }}{{ currencyLabel }}</p>
    </figcaption>

    <p v-if="slices.length === 0" class="pie-empty">{{ emptyText }}</p>

    <template v-else>
      <!-- role="img" + aria-label 挂在 canvas 容器上：canvas 里的内容读屏拿不到，
           这一句给出概览，逐项数值由下方列表承担 -->
      <div class="pie-canvas" role="img" :aria-label="description">
        <VChart class="pie-chart" :option="option" autoresize />
      </div>

      <!-- 图例兼表格视图：名称、占比、金额三列都由文字给出，颜色只是辅助的身份线索 -->
      <ul class="pie-legend">
        <li v-for="slice in slices" :key="slice.key" class="legend-row">
          <i
            class="legend-swatch"
            :style="{ backgroundColor: `var(${slice.colorToken})` }"
            aria-hidden="true"
          />
          <span class="legend-name">{{ slice.name }}</span>
          <span class="legend-percent">{{ formatPercent(slice.percent) }}</span>
          <span class="legend-amount">{{ formatAmount(slice.amount) }}</span>
        </li>
      </ul>
    </template>
  </figure>
</template>

<style scoped>
.category-pie {
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
  min-width: 0;
}

/* 标题行：左侧图名、右侧合计。合计用次要文字，不抢图名的位置 */
.pie-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 0.5rem;
}

.pie-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-heading);
  opacity: 0.88;
}

.pie-total {
  flex: none;
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  opacity: 0.7;
}

/* 画布高度固定，宽度铺满所在网格列；饼图在容器内取短边，故两列等宽时不会被拉扁 */
.pie-canvas {
  height: 148px;
}

/* vue-echarts 的根元素默认就是 block + 100%，此处显式写一遍以免样式表被裁剪时塌成 0 高 */
.pie-chart {
  width: 100%;
  height: 100%;
}

.pie-empty {
  padding: 0.5rem 0;
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.8;
}

.pie-legend {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

/* 四列：色块 / 名称 / 占比 / 金额。名称可截断，数字列不换行，窄列下也保持纵向对齐 */
.legend-row {
  display: grid;
  grid-template-columns: 10px minmax(0, 1fr) auto auto;
  align-items: center;
  gap: 0.4rem;
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

/* 色块与扇区共用一个令牌名：这里由浏览器解析 `var()`，canvas 用同一个名字解析出具体色值 */
.legend-swatch {
  width: 10px;
  height: 10px;
  border-radius: 2px;
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
