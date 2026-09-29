<script setup lang="ts">
/**
 * 折线图（Apache ECharts 6 封装）：把「日期文案 + 各系列取值」画成一张折线图。
 *
 * 首页两块走势图（总资产 / 收支）用的是同一张图——同一套坐标系、同一套配色规则、
 * 同一个悬停读数——差异只在标题、系列与概览数字，故绘图收在这一处，两处引用。
 * option 的构建与令牌解析在 `./trendChart.ts`（纯函数，不含 Vue），本文件只负责
 * 组件侧的三件事：**按需注册 ECharts 模块**、**跟随系统配色重绘**、**无障碍标签**。
 *
 * **按需注册**：ECharts 整包很大，本图只用到「折线 + 直角坐标系 + 提示气泡」三样，
 * 故只 `use()` 这几件。**不注册 `LegendComponent`**——图例由页面渲染（它还要带一行
 * 「最新日期 · 共 N 天 · 币种」，组件表达不了），注册了反而会出现两个图例。
 *
 * **配色跟随系统**：canvas 拿不到 `var(--color-accent)`，色值必须由 `readChartTheme()`
 * 从计算样式里读出来。系统在亮/暗之间切换时令牌值会变，但 canvas 不会自己知道，
 * 故监听 `prefers-color-scheme` 并重读、重建 option。
 *
 * **无障碍**：`aria-label` 由调用方传入（把周期、点数与最新一天的几个数字读出来），
 * 挂在外层容器上并声明 `role="img"`，读屏用户据此得到与图形等价的一句话。
 * 不启用 ECharts 自带的 aria（其默认即为关闭）——两套描述会重复播报，且组件生成的那份
 * 说不出「最新一天」这类业务口径。
 */

import { computed, onMounted, onUnmounted, ref } from 'vue'
import VChart from 'vue-echarts'
import { LineChart } from 'echarts/charts'
import { AxisPointerComponent, GridComponent, TooltipComponent } from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { buildTrendOption, readChartTheme } from './trendChart'
import type { ChartTheme, TrendChartOption, TrendSeriesSpec } from './trendChart'

use([CanvasRenderer, LineChart, GridComponent, TooltipComponent, AxisPointerComponent])

const props = defineProps<{
  /** 横轴各数据点的日期文案（按时间升序，缺日不补），与各系列的 `values` 一一对应。 */
  xLabels: readonly string[]
  /** 要画的折线。 */
  series: readonly TrendSeriesSpec[]
  /**
   * 读屏用的等价描述；由调用方按面板口径写好。
   *
   * **刻意不叫 `ariaLabel`**：`aria-` 是 Vue 的保留属性前缀，`v-bind` 到组件上的 `:aria-label`
   * 会被当成原生属性透传，不会落到同名 prop 上（读屏拿到的仍是组件外层那句，但 TS 会报缺参）。
   * 描述在此收下，再由模板写成根元素的 `aria-label`。
   */
  description: string
}>()

/**
 * 当前配色下的绘图主题。
 *
 * 放成 `ref` 而不是 `computed`：色值来自计算样式，Vue 追踪不到它的变化，
 * 只能由系统配色的监听主动重读后写回（见下）。
 */
const theme = ref<ChartTheme>(readChartTheme())

const option = computed<TrendChartOption>(() =>
  buildTrendOption({ xLabels: props.xLabels, series: props.series, theme: theme.value }),
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
  <!-- role="img" + aria-label 挂在容器上：canvas 里的内容读屏拿不到，这一句就是它的等价文本 -->
  <div class="trend-chart" role="img" :aria-label="description">
    <VChart class="trend-chart-canvas" :option="option" autoresize />
  </div>
</template>

<style scoped>
/* 高度由调用方给（首页按断点给了两个值），组件自身只负责铺满——写死高度会让窄屏规则无处安放 */
.trend-chart {
  width: 100%;
  height: 100%;
}

/* vue-echarts 的根元素默认就是 block + 100%，此处显式写一遍以免样式表被裁剪时塌成 0 高 */
.trend-chart-canvas {
  width: 100%;
  height: 100%;
}
</style>
