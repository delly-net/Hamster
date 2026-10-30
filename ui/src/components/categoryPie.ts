/**
 * 首页「收入构成 / 支出构成」饼图的纯数据与 option 构建（不含 Vue）。
 *
 * **输入是后端已经聚合好的「分类 → 金额」**（`GET /api/entries/category-totals`），
 * 本模块只做三件呈现层的事：
 *
 * 1. **把金额折成占比**：两个饼图各自除以本侧合计，故收入饼的百分比合计为 100%、
 *    支出饼的百分比合计也为 100%，两者不混算。
 * 2. **把分类折叠到最多 6 个扇区**：金额最大的若干分类各自成扇区，其余合并为「其他」。
 *    **「未分类」始终单独保留**——它是用户明确要看的一项（记账时分类可选），
 *    不因为金额小而沉进「其他」；分类数超过上限时由它顶掉第 5 名，而不是被折叠。
 * 3. **分配扇区颜色**：颜色令牌按 dataviz 校验过的固定顺序取用，**不循环复用**；
 *    「未分类」用中性灰（它没有类别身份），不占用分类色的序号。
 *
 * **颜色为什么按金额顺序分配**：这份数据没有「筛选后保留同一批系列」的交互——
 * 每次都是某个月全部可见分类的一个快照，且图例逐项列出名称、占比与金额，
 * 扇区身份靠文字而不只靠颜色。故这里按金额降序取色板的前几色，
 * 而不是为分类维护一套跨月恒定的色号（分类表本身没有颜色字段）。
 *
 * **canvas 不继承 CSS**：色值由 `TrendChart` 那套 `readChartTheme()` 读出后传入，
 * 模板里的图例色块用同一个令牌名交给浏览器解析——两处颜色只有一个真源。
 */

import type { PieSeriesOption } from 'echarts/charts'
import type { TooltipComponentOption } from 'echarts/components'
import type { ComposeOption } from 'echarts/core'
import type { EntryCategoryTotal } from '@/stores/categoryTotals'
import { formatAmount } from './entryFormat'
import type { ChartTheme, ChartToken } from './trendChart'

/**
 * 一张饼图最多呈现的扇区数（含「其他」）。
 *
 * 6 是 dataviz 给的上限：再多，扇区角度与颜色都难以在两两之间分辨，
 * 占比小的分类应合并而不是各占一条细丝。
 */
export const MAX_CATEGORY_SLICES = 6

/** 未分类的呈现文案（后端的 `categoryId` 为 `null`）。 */
export const UNCLASSIFIED_LABEL = '未分类'

/**
 * 「空值分项」在别处维度的默认称呼之外的用法：标签维度传「未打标签」。
 *
 * **不把这层差异做成第二个饼图模块**：两个维度的数据形状相同（一个 `null` 键 + 若干具名项），
 * 折叠、排序、百分比与配色的规则也逐条相同，差别只在「`null` 那一项叫什么」。
 * 调用方通过 {@link buildCategorySlices} 的 `nullLabel` 传入即可，默认仍是「未分类」。
 */
export const UNTAGGED_LABEL = '未打标签'

/** 折叠桶的呈现文案。 */
export const OTHER_LABEL = '其他'

/**
 * 分类色板的令牌顺序。
 *
 * **顺序即分配顺序，不得重排**（见 `base.css` 的色板注释）；超过 {@link MAX_CATEGORY_SLICES}
 * 时由折叠保证不会用到第 7 个之后的颜色，因此这里**不做取模循环**——循环会让两个扇区同色。
 */
const CATEGORY_COLOR_TOKENS: readonly ChartToken[] = [
  '--color-chart-1',
  '--color-chart-2',
  '--color-chart-3',
  '--color-chart-4',
  '--color-chart-5',
  '--color-chart-6',
  '--color-chart-7',
  '--color-chart-8',
]

/** 饼图的一个扇区（已折算占比、已定颜色）。 */
export interface CategoryPieSlice {
  /** 稳定的渲染 key：分类主键、`unclassified` 或 `other`。 */
  key: string
  /** 图例与 tooltip 上的名称；未分类固定为「未分类」。 */
  name: string
  /** 本侧合计中的金额，恒为正。 */
  amount: number
  /** 占本侧合计的百分比（0–100，不是 0–1 的比例）。 */
  percent: number
  /** 扇区颜色令牌名；图例色块与 canvas 共用。 */
  colorToken: ChartToken
  /** 是否为「其他」折叠桶。 */
  isOther: boolean
}

/** 构建一张饼图所需的输入。 */
export interface CategoryPieSpec {
  slices: readonly CategoryPieSlice[]
  theme: ChartTheme
  /** 系列名（如「收入构成」），供 ECharts 内部与 tooltip 兜底使用。 */
  title: string
}

/** 本图用到的 option 类型（按需注册的写法要求显式列出）。 */
export type CategoryPieOption = ComposeOption<PieSeriesOption | TooltipComponentOption>

/**
 * 金额降序；金额相同时未分类排最后，其余按分类主键升序。
 *
 * 后端已经按同一口径排过序，这里再排一次是为了让「折叠」与「取前几名」不依赖调用方的顺序
 * ——纯函数不假设上游永远记得排序。
 */
function compareTotals(left: EntryCategoryTotal, right: EntryCategoryTotal): number {
  if (right.amount !== left.amount) {
    return right.amount - left.amount
  }

  const leftUnclassified = left.categoryId === null
  const rightUnclassified = right.categoryId === null
  if (leftUnclassified !== rightUnclassified) {
    return leftUnclassified ? 1 : -1
  }

  return (left.categoryId ?? 0) - (right.categoryId ?? 0)
}

/**
 * 从全部分类里挑出要单独成扇区的那些，其余交给「其他」。
 *
 * 分类数不超过上限时全部单独呈现；超过时保留金额最大的 5 个，
 * 并在「未分类」没有进入前 5 时用**未分类顶掉第 5 名**（见模块头注释）。
 *
 * @returns `visible` 按金额降序；`folded` 为剩下的原始行。
 */
function pickVisible(sorted: readonly EntryCategoryTotal[]): {
  visible: EntryCategoryTotal[]
  folded: EntryCategoryTotal[]
} {
  if (sorted.length <= MAX_CATEGORY_SLICES) {
    return { visible: [...sorted], folded: [] }
  }

  // 6 个扇区里给「其他」留一格，故先取前 5 名
  const headLimit = MAX_CATEGORY_SLICES - 1
  const head = sorted.slice(0, headLimit)
  const unclassified = sorted.find((item) => item.categoryId === null)

  // 未分类在前 5 名之外：保留前 4 名 + 未分类，未分类因此始终是一个独立扇区
  const visible =
    unclassified !== undefined && !head.includes(unclassified)
      ? [...sorted.slice(0, headLimit - 1), unclassified].sort(compareTotals)
      : head

  const visibleSet = new Set(visible)
  const folded = sorted.filter((item) => !visibleSet.has(item))

  return { visible, folded }
}

/** 扇区的稳定 key。 */
function sliceKey(item: EntryCategoryTotal): string {
  return item.categoryId === null ? 'unclassified' : `category-${item.categoryId}`
}

/**
 * 把后端的分类合计折成饼图扇区：排序、折叠、算百分比、分配颜色。
 *
 * 金额非有限数或不为正的行会被丢弃——后端已经丢掉零合计，这里再挡一次是为了避免
 * 一个异常值把整张图的百分比算成 `NaN`（扇区角度会退化成不可见的 0，看起来像「没有这一类」）。
 *
 * @param items 本侧（收入或支出）的分类合计，顺序不限。
 * @param nullLabel 「空值分项」的呈现文案：分类维度是「未分类」，标签维度传「未打标签」。
 * @returns 最多 6 个扇区，按金额降序；无有效数据时为空数组。
 */
export function buildCategorySlices(
  items: readonly EntryCategoryTotal[],
  nullLabel: string = UNCLASSIFIED_LABEL,
): CategoryPieSlice[] {
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

  const { visible, folded } = pickVisible(sorted)
  const slices: CategoryPieSlice[] = []
  /** 下一个可用的分类色序号；未分类不占用序号（它用中性灰）。 */
  let paletteIndex = 0

  for (const item of visible) {
    const isUnclassified = item.categoryId === null
    // 未分类没有类别身份，用中性灰；其余按金额顺序依次取色。构造上最多 6 个扇区，不会越界
    const colorToken = isUnclassified
      ? '--color-chart-neutral'
      : (CATEGORY_COLOR_TOKENS[paletteIndex++] ?? '--color-chart-neutral')

    slices.push({
      key: sliceKey(item),
      name: item.categoryName ?? nullLabel,
      amount: item.amount,
      percent: (item.amount / total) * 100,
      colorToken,
      isOther: false,
    })
  }

  if (folded.length > 0) {
    const amount = folded.reduce((sum, item) => sum + item.amount, 0)
    const containsUnclassified = folded.some((item) => item.categoryId === null)

    slices.push({
      key: 'other',
      // 未分类顶掉第 5 名后，它不会落进折叠桶；保留这个判断是为了让折叠规则本身完整：
      // 万一上游改了「始终保留未分类」的规则，图例也不会悄悄把未分类藏进「其他」
      name: containsUnclassified ? `${OTHER_LABEL}（含${nullLabel}）` : OTHER_LABEL,
      amount,
      percent: (amount / total) * 100,
      colorToken: CATEGORY_COLOR_TOKENS[paletteIndex] ?? '--color-chart-neutral',
      isOther: true,
    })
  }

  return slices
}

/** 百分比呈现：一位小数，与图例、tooltip 共用。 */
const percentFormatter = new Intl.NumberFormat('zh-CN', {
  minimumFractionDigits: 1,
  maximumFractionDigits: 1,
})

/**
 * 格式化百分比。
 *
 * @param percent 0–100 的百分比数值（不是 0–1 的比例）。
 * @returns 形如 `42.1%` 的文本；非有限数按 `0.0%` 呈现，避免把 `NaN%` 画进图例。
 */
export function formatPercent(percent: number): string {
  return `${percentFormatter.format(Number.isFinite(percent) ? percent : 0)}%`
}

/** tooltip 里的 HTML 转义：分类名是用户输入的自由文本。 */
const HTML_ENTITIES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (char) => HTML_ENTITIES[char] ?? char)
}

/** 币种后缀：没有币种时不留一个孤零零的空格。 */
function currencySuffix(currencyCode: string | null): string {
  return currencyCode === null || currencyCode === '' ? '' : ` ${currencyCode}`
}

/**
 * 读屏用的一句话描述。
 *
 * 只报「几个扇区、合计多少」，不逐项念金额——逐项信息就在图下方的可见列表里，
 * 再念一遍会变成两倍长度；`role="img"` 的图需要的是概览，而不是第二份表格。
 *
 * @param title 图名（「收入构成」）。
 * @param period 周期文案（「2026年10月」）。
 * @param sliceCount 扇区个数（折叠时为 6，含「其他」）。
 * @param total 本侧合计金额。
 * @param currencyCode 币种代码；`null` 表示没有可用币种。
 * @returns 一句中文描述。
 */
export function buildCategoryPieLabel(
  title: string,
  period: string,
  sliceCount: number,
  total: number,
  currencyCode: string | null,
): string {
  return (
    `${period}${title}饼图，共 ${sliceCount} 个扇区，` +
    `合计 ${formatAmount(total)}${currencySuffix(currencyCode)}。` +
    '各扇区的名称、占比与金额见下方列表。'
  )
}

/**
 * 由扇区构建 ECharts option。
 *
 * **不画扇区标签与指引线**：名称、占比、金额都在图例里逐项列着，图上再挂一圈文字
 * 在窄面板里必然互相压叠。**扇区之间留 2px 底色缝隙**（`borderWidth: 2`，描边居中，
 * 相邻两扇区各让 1px），用来分隔相邻色块；tooltip 提供悬停读数，不是唯一读数通道。
 *
 * @param spec 扇区、主题与系列名。
 * @returns 可直接交给 `<v-chart :option>` 的 option。
 */
export function buildCategoryPieOption(spec: CategoryPieSpec): CategoryPieOption {
  const { slices, theme, title } = spec
  const { colors } = theme

  return {
    tooltip: {
      trigger: 'item',
      // 窄面板里让气泡始终落在图内，避免被卡片边缘裁掉
      confine: true,
      backgroundColor: colors['--color-background-soft'],
      borderColor: colors['--color-border'],
      textStyle: { color: colors['--color-text'], fontFamily: theme.fontFamily },
      formatter: (params: unknown) => {
        const index = (params as { dataIndex?: number }).dataIndex
        const slice = index === undefined ? undefined : slices[index]
        if (slice === undefined) {
          return ''
        }

        // 分类名是用户输入，必须转义后再拼进 HTML
        return (
          `${escapeHtml(slice.name)}<br/>` +
          `${escapeHtml(formatAmount(slice.amount))}（${escapeHtml(formatPercent(slice.percent))}）`
        )
      },
    },
    series: [
      {
        name: title,
        type: 'pie',
        // 饼图（不是环图）：从圆心铺到外沿；留 4% 余量给悬停放大与描边
        radius: '96%',
        center: ['50%', '50%'],
        // 标签与指引线一律不画，名称与数值由 DOM 图例承担（见模块头注释）
        label: { show: false },
        labelLine: { show: false },
        // 扇区之间用卡片底色描出一条缝，相邻扇区的色块因此不会糊在一起
        itemStyle: {
          borderColor: colors['--color-background-soft'],
          borderWidth: 2,
        },
        emphasis: {
          scale: true,
          scaleSize: 4,
        },
        data: slices.map((slice) => ({
          name: slice.name,
          value: slice.amount,
          itemStyle: {
            color: colors[slice.colorToken],
            borderColor: colors['--color-background-soft'],
            borderWidth: 2,
          },
        })),
      },
    ],
  }
}
