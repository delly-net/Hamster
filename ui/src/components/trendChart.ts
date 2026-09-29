/**
 * 首页折线图的 ECharts option 构建：把「日期文案 + 各系列取值」折成一份 ECharts option，
 * 外加两处 canvas 替代不了的东西——**语义令牌到色值的翻译**与**横轴标哪几天**。
 *
 * **为什么改用手写 SVG 之外的组件**：本模块的前身是 `lineChart.ts`——自己算纵轴范围、自己折 `path`、
 * 自己处理「整月只有一天」的退化。自维一套坐标系，代价是每个退化情形都得自己想到（历史上就漏过一次：
 * 纵轴只按净资产算范围，资产线一直画在框外，见 #67）。改用成熟组件后这些交给 ECharts，
 * 本模块只管它的「语言」与页面的「数据」之间那一层。
 *
 * **canvas 不继承 CSS**：ECharts 画在 canvas 上，`var(--color-accent)` 这类令牌它读不懂，
 * 坐标轴文字也不会继承 `body` 的字体。故颜色与字体一律在此处**显式读出**再喂进 option，
 * 并在系统配色切换时重读（切换由 `TrendChart.vue` 触发）。
 *
 * **不产出自带图例**：面板的图例右侧还有一行「最新日期 · 共 N 天 · 币种」，组件表达不了，
 * 故图例仍由页面渲染，此处既不注册 `LegendComponent`、也不写 legend 配置。
 * 为了让颜色不出现第二处定义，**系列只带令牌名**（{@link TrendSeriesSpec.colorToken}）：
 * 页面拿它给图例色块上色（`var(--token)`，浏览器负责解析），此处拿它解析出 canvas 色值，
 * 两处颜色因此只有一个真源。
 */

import type { LineSeriesOption } from 'echarts/charts'
import type {
  AxisPointerComponentOption,
  GridComponentOption,
  TooltipComponentOption,
} from 'echarts/components'
import type { ComposeOption } from 'echarts/core'
import { formatAmount } from './entryFormat'

/**
 * 本图用到的 option 类型（按需注册的写法要求显式列出，不写则整包类型都进来）。
 *
 * 直角坐标系与坐标轴的类型归在 `GridComponentOption` 里，故不另引 `XAXisOption` / `YAXisOption`。
 */
export type TrendChartOption = ComposeOption<
  GridComponentOption | TooltipComponentOption | AxisPointerComponentOption | LineSeriesOption
>

/**
 * 绘图用到的**语义令牌白名单**。
 *
 * 集中列出来的理由有二：一是解析令牌要造探针元素，逐个现取会反复创建/销毁 DOM；
 * 二是把「这张图用到了哪些设计令牌」变成可读的一句话——新增一种颜色必须同时出现在这里，
 * 免得某处悄悄引用了一个没被解析的令牌而拿到空字符串（canvas 拿到空色值只会画成黑色，不会报错）。
 */
export const CHART_TOKENS = [
  '--color-accent',
  '--color-transfer',
  '--color-income',
  '--color-expense',
  '--color-heading',
  '--color-text',
  '--color-border',
  '--color-border-hover',
  '--color-background-soft',
] as const

/** 令牌名（形如 `--color-accent`）。 */
export type ChartToken = (typeof CHART_TOKENS)[number]

/** 一次读取得到的绘图主题。 */
export interface ChartTheme {
  /** 令牌名 → 已解析成具体色值的字符串（`var()` 别名已展开）。 */
  colors: Record<ChartToken, string>
  /** canvas 文字用的字体栈，取自 `body` 的计算样式，与全站字体同源。 */
  fontFamily: string
}

/**
 * 读出当前配色下的绘图主题（色值 + 字体）。
 *
 * **必须走探针元素，不能读自定义属性**：`base.css` 里 `--color-expense: var(--color-danger)`、
 * `--color-background-soft: var(--hm-cream-soft)` 这类令牌的**值本身是 `var()` 引用**。
 * `getPropertyValue('--color-expense')` 拿到的是 `var(--color-danger)` 这串文本而不是颜色，
 * 直接喂给 canvas 会画成黑色。探针的写法让浏览器把整条引用链展开，拿到的必然是最终色值。
 *
 * @returns 已解析的颜色表与字体栈。
 */
export function readChartTheme(): ChartTheme {
  const probe = document.createElement('span')
  probe.setAttribute('aria-hidden', 'true')
  // 移出视口而不是 display:none：隐藏元素的计算样式在个别实现里会退化，而本函数只读颜色与字体，
  // 摆在与视口同宽的 0×0 盒子上即可，不参与布局也不可点。
  probe.style.cssText = 'position:absolute;left:-9999px;top:0;width:0;height:0;pointer-events:none'

  document.body.appendChild(probe)

  try {
    const colors = {} as Record<ChartToken, string>
    for (const token of CHART_TOKENS) {
      probe.style.color = `var(${token})`
      colors[token] = getComputedStyle(probe).color
    }

    return { colors, fontFamily: getComputedStyle(document.body).fontFamily }
  } finally {
    probe.remove()
  }
}

/** 一条待画的折线的说明。 */
export interface TrendSeriesSpec {
  /** 系列标识，同时用于 ECharts 的系列名与图例色块的 key。 */
  key: string
  /** 图例文案，也是 tooltip 里那行文字。 */
  label: string
  /** 颜色令牌名；图例色块与折线共用它，见模块头注释。 */
  colorToken: ChartToken
  /** 按时间升序的取值，与 {@link TrendChartSpec.xLabels} 一一对应。 */
  values: readonly number[]
}

/** 画一张折线图所需的全部输入。 */
export interface TrendChartSpec {
  /**
   * 横轴各数据点的日期文案（按时间升序）。
   *
   * **只含确实落过库的日子**：缺日不补零是后端口径（补零会把「这天还没结算」画成「这天为 0」，
   * 那是一根掉到底的线，属于错误信息），故横轴是**数据点序号**而不是日期序号。
   */
  xLabels: readonly string[]
  series: readonly TrendSeriesSpec[]
  theme: ChartTheme
}

/**
 * 取横轴的标注下标：左端、中间、右端三处。
 *
 * **横轴是数据点序号而不是日期序号**：缺日不补零，故两天之间有间隙时图上不会出现一段平地，
 * 代价是横轴的疏密与真实日期不完全等比——两端与中间标出实际日期作为补偿。
 *
 * 三处指向同一个下标时（整月只有一两天）只标一次，否则同一格文字叠三遍反而看不清。
 *
 * @param count 数据点个数。
 * @returns 需要显示标注的下标集合；个数为 0 时返回空集合。
 */
export function pickLabelIndices(count: number): Set<number> {
  if (count <= 0) {
    return new Set()
  }

  return new Set([0, Math.floor((count - 1) / 2), count - 1])
}

/**
 * 由数据点与系列说明构建 ECharts option。
 *
 * **纵轴范围交给 ECharts**（`scale: true`），不再自己算「极值 + 12% 余白」：ECharts 默认就按
 * **全部系列**的取值算范围，历史上那个「只按净资产算、资产线画到框外」的缺陷不会复现；
 * 代价是刻度值与网格密度会和手写实现不同，属预期的观感变化。
 *
 * **金额一律走 `formatAmount`**（两位小数，与后端 `decimal(...,2)` 对齐）——纵轴刻度与 tooltip
 * 都调它，避免出现第二种小数口径。
 *
 * @param spec 横轴文案、系列与主题。
 * @returns 可直接交给 `<v-chart :option>` 的 option。
 */
export function buildTrendOption(spec: TrendChartSpec): TrendChartOption {
  const { xLabels, series, theme } = spec
  const { colors } = theme
  const labelIndices = pickLabelIndices(xLabels.length)
  /** 只有一天时折线画不出线，只能靠数据点标记让那天看得见（ECharts 在 `showSymbol: false` 下会隐藏它）。 */
  const showSymbol = xLabels.length === 1

  return {
    // 轴文字现在画在 canvas 内，故用 containLabel 让容器把它们的宽度算进去，四周不再留白
    grid: { left: 0, right: 0, top: 8, bottom: 0, containLabel: true },
    xAxis: {
      type: 'category',
      // 折线两端顶到容器边缘，与改动前的观感一致（分类轴默认会在两端各留半格）
      boundaryGap: false,
      data: [...xLabels],
      // 只在左端、中间、右端标日期，其余留空——挤满一个月反而读不出是哪天
      axisLabel: {
        color: colors['--color-heading'],
        opacity: 0.65,
        fontFamily: theme.fontFamily,
        interval: (index: number) => labelIndices.has(index),
      },
      // 手写实现里只有横网格线，没有轴线与刻度；此处照旧，免得图被框起来
      axisLine: { show: false },
      axisTick: { show: false },
      splitLine: { show: false },
    },
    yAxis: {
      type: 'value',
      // 不强制包含 0：总资产这类量本来就离 0 很远，从 0 起画会把变化压成一条直线
      scale: true,
      // 三档刻度（两条分隔线），与改动前「上界 / 中值 / 下界」的疏密相当
      splitNumber: 2,
      axisLabel: {
        color: colors['--color-heading'],
        opacity: 0.65,
        fontFamily: theme.fontFamily,
        formatter: (value: number) => formatAmount(value),
      },
      axisLine: { show: false },
      axisTick: { show: false },
      splitLine: { lineStyle: { color: colors['--color-border'] } },
    },
    tooltip: {
      trigger: 'axis',
      // 十字准星：横线定位到该数据点的取值，竖线定位到是哪一天
      axisPointer: {
        type: 'cross',
        lineStyle: { color: colors['--color-border-hover'] },
        crossStyle: { color: colors['--color-border-hover'] },
        // 准星两端的小标签底色用面板底色、文字用标题色：亮暗两套下都是「深字浅底 / 浅字深底」
        label: {
          backgroundColor: colors['--color-background-soft'],
          color: colors['--color-heading'],
          borderColor: colors['--color-border'],
        },
      },
      backgroundColor: colors['--color-background-soft'],
      borderColor: colors['--color-border'],
      textStyle: { color: colors['--color-text'], fontFamily: theme.fontFamily },
      // 金额格式化只此一处：刻度与提示气泡共用 formatAmount，不出现第二种小数口径
      valueFormatter: (value: unknown) =>
        typeof value === 'number' ? formatAmount(value) : String(value),
    },
    series: series.map((item) => ({
      name: item.label,
      type: 'line',
      data: [...item.values],
      // 一个月几十个点全画标记会糊成一片，故只在悬停（emphasis）时显示；单点时例外，见上
      showSymbol,
      symbolSize: 8,
      // 折线不取直以外的插值：平滑曲线会把「相邻两天之间」画成没发生过的弧，读数的人会当真
      smooth: false,
      lineStyle: { width: 2, color: colors[item.colorToken] },
      itemStyle: { color: colors[item.colorToken] },
      // 系列名即图例文案，tooltip 与图例因此说的是同一句话
      emphasis: { focus: 'none' },
    })),
  }
}
