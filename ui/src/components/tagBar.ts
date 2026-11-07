/**
 * 报表页「标签构成」横向条形图的纯数据与 option 构建（不含 Vue）。
 *
 * **输入是后端已经聚合好的「标签 → 金额」**（日报表 / 月报表同一次请求里的 tags 侧），
 * 本模块只做三件呈现层的事：
 *
 * 1. **排序与百分比**：金额降序、金额相同时未打标签排最后，其余按标签主键升序
 *    （与分类饼图共用 {@link compareTotals}）；百分比以**标签金额之和**为分母，
 *    而不是区间收支合计——一笔交易挂多个标签时金额在每个标签下各计一次，
 *    两者本来就可能不等（页面已在面板说明里写明）。
 * 2. **全部列出**：与分类饼图的「前 5 + 其他」不同，标签条形图不折叠，有多少画多少；
 *    标签多时画布高度随条数增长，不做静默截断。
 * 3. **单系列配色**：条形图按「收入 / 支出」各成一张，颜色由调用方指定语义令牌
 *    （`--color-income` / `--color-expense`），不按标签分配分类色板——标签身份由纵轴名称
 *    与图下列表承担，颜色只表达这是收入还是支出。
 *
 * **canvas 不继承 CSS**：色值由 `readChartTheme()` 读出后传入（与 `trendChart.ts` 同一方案）。
 */

import type { BarSeriesOption } from 'echarts/charts'
import type { GridComponentOption, TooltipComponentOption } from 'echarts/components'
import type { ComposeOption } from 'echarts/core'
import type { EntryCategoryTotal } from '@/stores/categoryTotals'
import { compareTotals, currencySuffix, escapeHtml, formatPercent } from './categoryPie'
import { formatAmount } from './entryFormat'
import type { ChartTheme, ChartToken } from './trendChart'

/** 本图用到的 option 类型（按需注册的写法要求显式列出）。 */
export type TagBarOption = ComposeOption<
  BarSeriesOption | GridComponentOption | TooltipComponentOption
>

/** 一根横条（已排序、已算占比）。 */
export interface TagBar {
  /** 稳定的渲染 key：`tag-<主键>` 或 `untagged`。 */
  key: string
  /** 纵轴与图下列表上的名称；未打标签固定为调用方给的 `nullLabel`。 */
  name: string
  /** 该标签下的金额，恒为正。 */
  amount: number
  /** 占「标签金额之和」的百分比（0–100，不是 0–1 的比例）。 */
  percent: number
}

/** 画一张标签条形图所需的输入。 */
export interface TagBarSpec {
  bars: readonly TagBar[]
  theme: ChartTheme
  /** 系列名（如「收入标签构成」），供 ECharts 内部与 tooltip 兜底使用。 */
  title: string
  /** 条形颜色令牌名（收入 / 支出语义色）。 */
  colorToken: ChartToken
}

/** 一根横条的稳定 key。 */
function barKey(item: EntryCategoryTotal): string {
  return item.categoryId === null ? 'untagged' : `tag-${item.categoryId}`
}

/**
 * 把后端的标签合计折成条形图的横条：过滤异常金额、排序、算占比。
 *
 * 金额非有限数或不为正的行会被丢弃——后端已经丢掉零合计，这里再挡一次是为了避免
 * 一个异常值把分母算成 `NaN`。
 *
 * @param items 本侧（收入或支出）的标签合计，顺序不限。
 * @param nullLabel 「空值分项」的呈现文案：标签维度传「未打标签」。
 * @returns 全部有效标签，按金额降序；无有效数据时为空数组。
 */
export function buildTagBarItems(
  items: readonly EntryCategoryTotal[],
  nullLabel: string,
): TagBar[] {
  const sorted = items
    .filter((item) => Number.isFinite(item.amount) && item.amount > 0)
    .slice()
    .sort(compareTotals)

  if (sorted.length === 0) {
    return []
  }

  const total = sorted.reduce((sum, item) => sum + item.amount, 0)
  if (!Number.isFinite(total) || total <= 0) {
    return []
  }

  return sorted.map((item) => ({
    key: barKey(item),
    name: item.categoryName ?? nullLabel,
    amount: item.amount,
    percent: (item.amount / total) * 100,
  }))
}

/**
 * 由横条构建 ECharts option。
 *
 * **横向条形图**：标签名在纵轴，金额在横轴，`inverse: true` 让金额最大的排在最上面；
 * 标签名过长时截断，完整名称由 tooltip 与图下列表给出。**单系列**故不注册图例——
 * 一张图只表达「收入」或「支出」，标题已经说明。
 *
 * @param spec 横条、主题、系列名与颜色令牌。
 * @returns 可直接交给 `<v-chart :option>` 的 option。
 */
export function buildTagBarOption(spec: TagBarSpec): TagBarOption {
  const { bars, theme, title, colorToken } = spec
  const { colors } = theme
  const barColor = colors[colorToken]

  return {
    // containLabel 让纵轴标签宽度被算进容器，避免长标签被裁掉一截
    grid: { left: 0, right: 12, top: 8, bottom: 0, containLabel: true },
    xAxis: {
      type: 'value',
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
    yAxis: {
      type: 'category',
      // 金额最大的排在最上面（分类轴默认从下往上）
      inverse: true,
      data: bars.map((bar) => bar.name),
      axisLabel: {
        color: colors['--color-heading'],
        opacity: 0.8,
        fontFamily: theme.fontFamily,
        // 标签名是用户输入的自由文本：过长时截断，完整名称见 tooltip 与图下列表
        width: 120,
        overflow: 'truncate',
      },
      axisLine: { show: false },
      axisTick: { show: false },
      splitLine: { show: false },
    },
    tooltip: {
      trigger: 'item',
      // 窄面板里让气泡始终落在图内，避免被卡片边缘裁掉
      confine: true,
      backgroundColor: colors['--color-background-soft'],
      borderColor: colors['--color-border'],
      textStyle: { color: colors['--color-text'], fontFamily: theme.fontFamily },
      formatter: (params: unknown) => {
        const index = (params as { dataIndex?: number }).dataIndex
        const bar = index === undefined ? undefined : bars[index]
        if (bar === undefined) {
          return ''
        }

        // 标签名是用户输入，必须转义后再拼进 HTML
        return (
          `${escapeHtml(bar.name)}<br/>` +
          `${escapeHtml(formatAmount(bar.amount))}（${escapeHtml(formatPercent(bar.percent))}）`
        )
      },
    },
    series: [
      {
        name: title,
        type: 'bar',
        data: bars.map((bar) => bar.amount),
        // 细条：条数多时不至于把画布糊成一片色块
        barMaxWidth: 16,
        itemStyle: {
          color: barColor,
          // 横向条形图只圆「数据端」（右端），起点仍贴住 0 基线
          borderRadius: [0, 4, 4, 0],
        },
        emphasis: { itemStyle: { color: barColor } },
      },
    ],
  }
}

/**
 * 读屏用的一句话描述。
 *
 * 只报「几个标签、合计多少」，不逐项念金额——逐项信息就在图下方的可见列表里。
 *
 * @param title 图名（「收入标签构成」）。
 * @param period 周期文案（「2026年10月」）。
 * @param count 标签条数。
 * @param total 标签金额之和。
 * @param currencyCode 币种代码；`null` 表示没有可用币种。
 * @returns 一句中文描述。
 */
export function buildTagBarLabel(
  title: string,
  period: string,
  count: number,
  total: number,
  currencyCode: string | null,
): string {
  return (
    `${period}${title}条形图，共 ${count} 个标签，` +
    `合计 ${formatAmount(total)}${currencySuffix(currencyCode)}。` +
    '各标签的名称、占比与金额见下方列表。'
  )
}
