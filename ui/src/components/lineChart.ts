/**
 * 折线图的几何计算：把若干条数值序列折算成 SVG `path` 的 `d` 与纵轴刻度。
 *
 * 首页有两个面板（总资产走势、收支走势）画的是同一种图——同一套坐标系、同一套留白与刻度规则、
 * 同一个「单点补一小段横线」的退化处理——故几何计算收在这一处，页面只负责把序列喂进来、
 * 把结果画出去。
 *
 * **坐标系是逻辑值 0~100（见 {@link VIEW_SIZE}）**，实际像素由 CSS 决定，
 * 故缩放交给 `preserveAspectRatio="none"`；代价是线条会被横向拉粗，
 * 用 `vector-effect="non-scaling-stroke"` 把描边宽度固定在设备像素上抵消它。
 * **轴标签一律画在 SVG 之外**（HTML 元素）：SVG 被拉伸后里面的文字会变形，
 * 而 HTML 文本不受影响、还能继承全站的字体与配色令牌——故本模块只算几何，不产出任何文字。
 *
 * 刻意**不引图表库**：本项目的依赖只有 vue / vue-router / pinia，
 * 为两条折线引入一个库（连同它的主题、响应式与打包体积）不划算。
 */

/** 图表的逻辑坐标系边长：`viewBox` 用 0~100 的方形，实际宽高交给 CSS。 */
export const VIEW_SIZE = 100

/** 纵轴上下的留白比例：折线贴着画布边缘不好看，也容易被描边裁掉。 */
const PADDING_RATIO = 0.12

/** 网格线（也就是纵轴刻度）的纵向位置比例：上界、中值、下界。 */
const TICK_RATIOS = [0, 0.5, 1] as const

/** 一条待画的折线的说明。 */
export interface LineChartSeriesSpec<T> {
  /** 系列标识，同时用于图例色块与折线的样式类后缀。 */
  key: string
  /** 图例文案。 */
  label: string
  /** 从数据点取出本系列的取值。传函数而不是「一串现成的数字」，见 {@link buildLineChart}。 */
  pick: (day: T) => number
}

/** 画好的一条折线。 */
export interface LineChartSeries {
  key: string
  label: string
  /** `path` 的 `d` 属性（逻辑坐标系下的折线）。 */
  path: string
}

/** 纵轴的一个刻度。 */
export interface LineChartTick {
  /** 逻辑坐标（0 为画布顶端）。 */
  y: number
  /** 该处对应的金额。 */
  value: number
}

/** 图表的几何结果。 */
export interface LineChart {
  series: LineChartSeries[]
  /** 纵轴刻度（上界、中值、下界），与网格线一一对应。 */
  ticks: LineChartTick[]
}

/**
 * 由数据点与系列说明算出图表的几何。
 *
 * **纵轴范围取全部系列的极值**，再各留一点余白：同一张图上的几条线共用一根纵轴才可比，
 * 各用各的轴会让「净资产低于总资产」这类事实在图上消失。因此**每个系列的取值都要参与极值计算**
 * ——只取其中一条会让另一条画到框外去（首页此前的总资产图就只按净资产算了范围，
 * 而总资产恒不低于净资产，于是资产线一直顶在框外）。
 *
 * 所有取值相同时（如整月只有一天）给一个对称的人工范围，避免除零。
 *
 * @param days 按时间升序的数据点；**为空时返回 `null`**（没有数据就什么都不画）。
 * @param specs 系列说明；**为空时返回 `null`**。取值一律由 `pick` 现取，
 *   故「取值个数与数据点个数不等」这类错配在类型上就不可能出现。
 * @returns 折线与刻度；无数据时为 `null`。
 */
export function buildLineChart<T>(
  days: readonly T[],
  specs: readonly LineChartSeriesSpec<T>[],
): LineChart | null {
  if (days.length === 0 || specs.length === 0) {
    return null
  }

  const values = specs.flatMap((spec) => days.map((day) => spec.pick(day)))
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
  const buildPath = (pick: (day: T) => number): string => {
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
    series: specs.map((spec) => ({
      key: spec.key,
      label: spec.label,
      path: buildPath(spec.pick),
    })),
    ticks: TICK_RATIOS.map((ratio) => ({
      y: ratio * VIEW_SIZE,
      value: max - ratio * (max - min),
    })),
  }
}

/**
 * 取横轴的三处标注：左端、中间、右端。
 *
 * **横轴是数据点序号而不是日期序号**：缺日不补零，两天之间有间隙时图上不会出现一段平地，
 * 代价是横轴的疏密与真实日期不完全等比——两端与中间标出实际日期作为补偿。
 *
 * 重复的日期只标一次（整月只有一两天时三处会指向同一天，标三遍反而看不清），
 * 空位留 `null` 以保持三列对齐。
 *
 * @param labels 按时间升序的标签，通常就是日期（`yyyy-MM-dd`）。
 * @returns 三个位置上的标签；无数据时为空数组。
 */
export function pickAxisLabels(labels: readonly string[]): (string | null)[] {
  const first = labels[0]
  const last = labels[labels.length - 1]
  if (first === undefined || last === undefined) {
    return []
  }

  const middle = labels[Math.floor((labels.length - 1) / 2)] ?? first
  const picked = [first, middle, last]

  return picked.map((label, index) => (picked.indexOf(label) === index ? label : null))
}
