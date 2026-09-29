<script setup lang="ts">
/**
 * `/` 首页（概览）：当前账套本月的**总资产走势**、**收支走势**两个图表面板，
 * 以及其下的**最近交易**只读列表。
 *
 * 两块图表数据都来自后端落库的**按天记录**（总资产结算订阅、收入结算订阅、支出结算订阅各自在结算
 * 事件后逐日重算）。「哪些账户算资产、哪些算负债、个人与公共如何相加、收支的符号」全部由后端一处
 * 定义（见 `ITotalAssetSettlementService` / `IIncomeSettlementService` / `IExpenseSettlementService`），
 * 前端再算一遍就会出现「首页的曲线与账户页、明细页对不上」这种谁也说不清的问题。
 * 图表部分只做一件事：把两条序列画出来。
 *
 * **本页只有一处相加**：收支面板标题下那三个概览数字是**本月合计**——把该面板已落库的按天记录
 * 加起来（口径见执行规范 #67「月度概览只做同口径按天相加并注明来源」，界面上一行小字写明口径）。
 * 总资产面板的三个数字则是**时点余额**（最新一天的总资产 / 净资产 / 负债），逐日相加没有业务含义
 * ——「本月总资产合计」不是一个数——故那里原样取最新一个数据点。除此之外本页不做任何金额汇总。
 *
 * **两个面板共用一张图与一套模板**：折线交给 `components/TrendChart.vue`（Apache ECharts 6 的封装，
 * 几何、刻度、退化处理都由它负责），面板本身由 {@link panels} 描述后交给同一个 `v-for` 渲染。
 * 两份「长得一样但各写一遍」的图迟早会在留白、刻度或退化处理上分叉，
 * 而它们恰恰是必须逐字相同的部分（两个面板上下叠着，差一个像素都看得出来）。
 * 各面板**自己的**东西——标题、系列、概览数字、无障碍描述、空状态文案——仍各写各的
 * （在 {@link assetsPanel} / {@link flowsPanel} 里），那几处本来就该不同。
 *
 * 本页只把**序列喂进去**：系列的颜色只写**令牌名**（`--color-accent` 这类），
 * 图例色块与本页样式用 `var(--token)` 上色、`TrendChart` 用同一个名字解析出 canvas 色值，
 * 于是「哪条线是什么颜色」全站只有一个定义处。
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
 *
 * **第三块面板是「最近交易」**：与上面两块**没有任何数据关系**——它不依赖结算订阅，
 * 直接把当前账套里最新的若干条明细列出来（`GET /api/entries?order=desc`），故账套刚建好、
 * 一次结算都还没走完时，上面两块是空的而这里是满的：这不是矛盾，是「结算数据」与「原始明细」
 * 两种时延的如实呈现。
 *
 * 它**只读**：不在首页给改账入口，也不重复明细页的筛选与分页——首页要回答的是「最近发生了什么」，
 * 而不是「我想查哪一段」，后者已经有 `/entries` 一页，标题行右侧那个链接就是通往它的路。
 * 行粒度、金额口径、对手方与分类/标签的文案**全部与明细页共用同一份实现**
 * （`components/entryFormat`）：两页各写一份的话，「账本账户叫什么」「两位小数怎么格式化」
 * 迟早会分叉成两种说法。
 */

import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { ApiError } from '@/api/http'
import { formatAmount } from '@/components/entryFormat'
import RecentEntryList from '@/components/RecentEntryList.vue'
import TrendChart from '@/components/TrendChart.vue'
import type { TrendSeriesSpec } from '@/components/trendChart'
import { useAccountSetsStore } from '@/stores/accountSets'
import { RECENT_ENTRY_LIMIT, useEntriesStore } from '@/stores/entries'
import { useIncomeExpensesStore } from '@/stores/incomeExpenses'
import { useTotalAssetsStore } from '@/stores/totalAssets'

const accountSets = useAccountSetsStore()
const totalAssets = useTotalAssetsStore()
const incomeExpenses = useIncomeExpensesStore()
const entries = useEntriesStore()

/** 日期文本（`yyyy-MM-dd`）的解析式，用于取「几月几日」。 */
const DAY_PATTERN = /^\d{4}-(\d{2})-(\d{2})$/

/** 当前账套是否已选定；未选定时本页只提示、不请求。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/**
 * 三块面板各自的失败文案；无失败时为空串。
 *
 * **分开存**：一块挂了不该把另外两块也变成错误——总资产接口的一次超时，
 * 没有理由让已经查得到的明细列表变成一片空白。
 */
const errors = ref<{ assets: string; flows: string; recent: string }>({
  assets: '',
  flows: '',
  recent: '',
})

/** 任一接口在途（刷新按钮的禁用与文案据此判断）。 */
const loading = computed(
  () => totalAssets.loading || incomeExpenses.loading || entries.recentLoading,
)

/** 一个概览数字（面板顶部那三个之一）。 */
interface StatView {
  label: string
  /** 已经格式化好的金额文案。 */
  value: string
}

/** 一个面板里图表部分的呈现数据；无数据时整个 `chart` 为 `null`。 */
interface ChartView {
  series: TrendSeriesSpec[]
  /**
   * 横轴各数据点的日期文案（按时间升序，**缺日不补**），与各系列的 `values` 一一对应。
   *
   * 标哪几天由 `TrendChart` 决定（左端、中间、右端），本页只负责把日期写成 `M月D日`。
   */
  xLabels: string[]
  /**
   * 概览数字。**两个面板的口径不同**：总资产面板取**最新一个数据点**（三个数都是时点余额），
   * 收支面板取**本月合计**（三个数都是按天记录相加，见 {@link flowsPanel}）。
   */
  stats: StatView[]
  /**
   * 概览数字的口径说明，渲染在三个数字下方的一行次要小字。
   *
   * **只有需要写明口径的面板才给**：收支面板的数字是加出来的，不写一句「这是本月合计」，
   * 用户无法从界面上分辨它和当日值；总资产面板是时点余额、读数即所得，不写。
   */
  statNote?: string
  /** 图例右侧的元信息（最新日期 · 天数 · 币种）。 */
  meta: string
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

/**
 * 总资产面板。
 *
 * **两条线共用一根纵轴**：`TrendChart` 只声明一根 `yAxis`，故「净资产低于总资产」这个事实在图上
 * 始终成立——一条线一根轴会让它消失（这也是全站不引入双轴图的原因）。
 *
 * **三个概览数字取自最新一个数据点**：总资产、净资产、负债都是**时点余额**，说的是「到这一天为止
 * 账上还剩多少」，逐日相加没有业务含义（「本月总资产合计」不是一个数）。故这里既不平摊也不求和，
 * 也不给口径小字——读数即所得。下方收支面板是另一套口径（本月合计），那里才有说明。
 */
function assetsPanel(): Panel {
  const days = totalAssets.daily?.days ?? []
  const currencyLabel = totalAssets.daily?.currencyCode ?? ''
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
      latest === undefined
        ? null
        : {
            series: [
              // 总资产 = 资金账户合计（不含负债）；净资产 = 总资产 + 负债（负债带符号，故此处是加）
              {
                key: 'asset',
                label: '总资产',
                colorToken: '--color-accent',
                values: days.map((day) => day.assetTotal),
              },
              {
                key: 'net',
                label: '净资产',
                colorToken: '--color-transfer',
                values: days.map((day) => day.netTotal),
              },
            ],
            stats: [
              { label: '总资产', value: formatAmount(latest.assetTotal) },
              { label: '净资产', value: formatAmount(latest.netTotal) },
              { label: '负债合计', value: formatAmount(latest.liabilityTotal) },
            ],
            meta: buildMeta(formatDay(latest.date), days.length, currencyLabel),
            xLabels: days.map((day) => formatDay(day.date)),
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
 *
 * **三个概览数字是本月合计，不是最新一天的值**（本页唯一一处相加）：接口下发的每个数据点都是
 * **当日发生额**（见 `IncomeExpenseDaily` 的类型注释），故逐日相加即本月累计；
 * 口径由执行规范 #67 定在前端——「月度概览只做同口径按天相加并注明来源」。
 * 不为此另开后端汇总接口：同一份按天记录再让后端算一遍，等于给同一个数字立第二个真源；
 * 界面上那行 `statNote` 就是「注明来源」那半句。
 *
 * **合计不是「整月合计」**：`days` 缺日不补零（后端口径），故相加得到的只是**已落库的那些天**
 * 之和，即「截至最新结算日的本月累计」。未结算的日子不计入是**如实呈现**，不是漏算。
 */
function flowsPanel(): Panel {
  const days = incomeExpenses.daily?.days ?? []
  const currencyLabel = incomeExpenses.daily?.currencyCode ?? ''
  const latest = days[days.length - 1]

  // 逐日金额最多两位小数、一个自然月最多 31 个数据点，浮点累加的误差远小于两位小数的呈现精度
  const monthIncome = days.reduce((sum, day) => sum + day.incomeTotal, 0)
  const monthExpense = days.reduce((sum, day) => sum + day.expenseTotal, 0)
  // 净额取「收入合计 − 支出合计」而不是逐日累加 `netTotal`：两者在十进制下恒等，
  // 但取差保证界面上「净额」永远是**所显示的那两个数**之差，不会有末位漂移落成一分钱
  const monthNet = monthIncome - monthExpense

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
      latest === undefined
        ? null
        : {
            series: [
              {
                key: 'income',
                label: '收入',
                colorToken: '--color-income',
                values: days.map((day) => day.incomeTotal),
              },
              {
                key: 'expense',
                label: '支出',
                colorToken: '--color-expense',
                values: days.map((day) => day.expenseTotal),
              },
            ],
            stats: [
              { label: '收入', value: formatAmount(monthIncome) },
              { label: '支出', value: formatAmount(monthExpense) },
              { label: '净额', value: formatAmount(monthNet) },
            ],
            statNote: '三个数字是本月合计：按天记录逐日相加，尚未结算的日子不计入。',
            meta: buildMeta(formatDay(latest.date), days.length, currencyLabel),
            xLabels: days.map((day) => formatDay(day.date)),
            // 读屏用户拿不到那行口径小字的位置关系，故口径（本月合计 + 截至哪一天）必须写进这一句里，
            // 让他们听到的数字与视觉上的数字是同一个
            ariaLabel:
              `${period.value}收入与支出走势图，共 ${days.length} 个数据点。` +
              `本月合计（截至 ${formatDay(latest.date)}）收入 ${formatAmount(monthIncome)}、` +
              `支出 ${formatAmount(monthExpense)}、` +
              `净额 ${formatAmount(monthNet)}` +
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

/** 把 `yyyy-MM-dd` 显示成 `M月D日`；无法解析时原样回显。 */
function formatDay(date: string): string {
  const matched = DAY_PATTERN.exec(date)
  if (matched === null) {
    return date
  }

  return `${Number(matched[1])}月${Number(matched[2])}日`
}

/**
 * 拉取两块当月序列与最近交易。
 *
 * **不传月份**，由后端取服务器本地的当月：记录的日期是本地日期，
 * 跟着服务器走才不会与落库的日期错位（前端按浏览器时区算一个月，跨时区部署时就会差一天）。
 *
 * 三个请求**并发**发出、**各自**记账自己的失败：串行会让后面的面板白等前面的网络往返，
 * 而用 `Promise.all` 的「一票否决」语义则会让总资产接口的一次超时把收支面板与明细列表也
 * 一起变成空白。
 */
async function load(): Promise<void> {
  if (!hasAccountSet.value) {
    return
  }

  errors.value = { assets: '', flows: '', recent: '' }

  await Promise.all([
    totalAssets.loadDaily().catch((error: unknown) => {
      errors.value.assets = error instanceof ApiError ? error.message : '加载总资产失败'
    }),
    incomeExpenses.loadDaily().catch((error: unknown) => {
      errors.value.flows = error instanceof ApiError ? error.message : '加载收支失败'
    }),
    entries.loadRecent().catch((error: unknown) => {
      errors.value.recent = error instanceof ApiError ? error.message : '加载最近交易失败'
    }),
  ])
}

// 账套切换后必须重来一遍：三块数据都按账套隔离，且**图表记录按用户分行**——
// 同一本账套里换个人看到的个人账户不同，故结果是「这个账套里、我这个人的」那一份；
// 明细同样按账套隔离（可见性由后端按当前用户判定）。
// 未选择账套时清空，避免退出登录后仍残留可见数据。
//
// 这里清的是**首页自己那一份**（`clearRecent`），不碰明细页的 `clear`：两个页面的生命周期
// 互不相干（同一时刻只会挂载一个），替对方清结果没有收益，还会让「谁在什么时候清了什么」难追。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    totalAssets.clear()
    incomeExpenses.clear()
    entries.clearRecent()
    errors.value = { assets: '', flows: '', recent: '' }

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
          故最新一天通常是上一个结算日，而不是今天。页面底部另给出当前账套最近的
          {{ RECENT_ENTRY_LIMIT }} 条账目明细，那一块取的是原始明细、与结算无关。
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
          <!-- 口径小字是数字的注脚而不是另一块内容，故放在 .stats 里占满一行（见样式），
               而不是面板的兄弟块——后者会与数字之间拉开面板的 0.9rem 块距。
               只有给了 statNote 的面板才渲染（总资产面板是时点值，不写口径） -->
          <div class="stats">
            <div v-for="stat in panel.chart.stats" :key="stat.label" class="stat">
              <span class="stat-label">{{ stat.label }}</span>
              <strong class="stat-value">{{ stat.value }}</strong>
            </div>
            <p v-if="panel.chart.statNote" class="stat-note">{{ panel.chart.statNote }}</p>
          </div>

          <div class="legend">
            <span v-for="item in panel.chart.series" :key="item.key" class="legend-item">
              <!-- 色块与折线共用一个令牌名：这里由浏览器解析 `var()`，TrendChart 把同一个名字
                   解析成 canvas 认得的具体色值。两处颜色因此只有一个定义处 -->
              <i class="swatch" :style="{ backgroundColor: `var(${item.colorToken})` }" />
              {{ item.label }}
            </span>
            <span class="legend-meta">{{ panel.chart.meta }}</span>
          </div>

          <!-- 折线的绘制（坐标系、刻度、悬停读数）全部交给组件；本页只把序列喂进去 -->
          <div class="chart">
            <TrendChart
              :series="panel.chart.series"
              :x-labels="panel.chart.xLabels"
              :description="panel.chart.ariaLabel"
            />
          </div>
        </template>
      </section>

      <!--
        最近交易：**排在两张走势图之后**——本页的身份是「走势概览」，明细是补充而不是主体。
        整块**只读**：不给改账入口、不引入弹窗，也不重复明细页的筛选与分页，
        要看全部或要改账都走标题行右侧那个链接。
        行的呈现交给 `RecentEntryList`（记账页的「最近 N 次同类型交易」用的是同一个组件，
        两处对同一份明细的读法不会分叉）；本页只负责取数与三态里的前两条。
      -->
      <section class="panel">
        <div class="panel-head">
          <div>
            <h2 class="panel-title">最近交易</h2>
            <p class="panel-note">
              按发生时间倒序，最多 {{ RECENT_ENTRY_LIMIT }} 条；只含资金账户与负债账户上的明细
              （与「账目明细」页同一口径，一笔转账会占两行）。
            </p>
          </div>
          <!-- 次要按钮的样子与页头【刷新】同源（同一套 .ghost 规则）；这里是链接而非按钮，
               点击只做路由跳转，不发任何请求 -->
          <RouterLink class="ghost" :to="{ name: 'entries' }">查看全部明细</RouterLink>
        </div>

        <!-- 三态与上面两块面板同一套写法：失败、加载中、无数据。
             空态文案**不复用 emptyHint**：那个函数里的「还没有可用币种」分支对明细不成立
             ——明细没有币种维度，套用会得到一句与事实无关的提示。 -->
        <p v-if="errors.recent !== ''" class="error">{{ errors.recent }}</p>
        <p v-else-if="entries.recentItems === null" class="hint">加载中…</p>

        <!-- 空态与列表都由组件承担（`emptyText` 由本页给：明细没有币种维度，那句空态不能复用
             `emptyHint` 里任何一条与币种有关的说法）。本页仍传 `show-type`：这块面板混排三种类型，
             交易类型标签是用户区分「账户之间的搬运」与「真正的收支」的唯一线索 -->
        <RecentEntryList
          v-else
          :entries="entries.recentItems"
          empty-text="当前账套还没有账目明细。"
          show-type
        />
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

/* 次要按钮：与账户页 / 明细页的「重置」同源（描边主色、无底色，hover 才铺淡底）。
   它同时被页头的【刷新】（button）与最近交易面板的【查看全部明细】（RouterLink）使用：
   `text-decoration: none` 只对后者有意义，对前者是无副作用的空设——故写在这里一处，
   而不是为链接另立一个长得一样的类。 */
.ghost {
  flex: none;
  padding: 0.3rem 0.7rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: none;
  color: var(--color-accent-strong);
  font-family: inherit;
  font-size: 12.5px;
  text-decoration: none;
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

/* 面板头：左侧「标题 + 它的说明」、右侧一个动作（最近交易面板的【查看全部明细】）。
   两张走势图的面板没有右侧动作，故没有这个容器——不为对称而给它们套一层空壳。 */
.panel-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

/* 面板内的一行说明：与明细页的 .picker-hint / .save-note 同档（12.5px / 1.7 / 0.7），
   全站的「次要小字」只有这一种写法 */
.panel-note {
  margin-top: 0.25rem;
  font-size: 12.5px;
  line-height: 1.7;
  opacity: 0.7;
}

/*
 * 面板内的状态文案（加载中 / 失败）：卡片自己已经有描边与底色，这两行再各套一层框
 * 会变成「框里套框」，故在此把外边距与底色让掉，只保留文字本身。
 * 另一条状态文案——「最近交易」的空态——由 `RecentEntryList` 呈现，它渲染的节点拿不到本组件的
 * scoped 标记（片段根），这条选择器够不着，故那边的无框样式写在组件自己身上。
 */
.panel > .hint,
.panel > .error {
  padding: 0.15rem 0;
  border: none;
  border-radius: 0;
  background: none;
}

/* 三个概览数字：等宽分栏，窄屏自动折行。口径小字（.stat-note）也挂在这里，
   故列间距（2rem）对它无效、行间距（0.5rem）正好当它和数字之间的那点距离 */
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

/* 概览数字的口径说明（收支面板的「本月合计」）。文案沿用全站的「次要小字」档
   （12.5px / 1.7 / 0.7，与 .panel-note 同源），不另立一套字号。
   `flex-basis: 100%` 让它整行独占、落在三个数字的下一行：与数字之间的距离于是取 .stats 的
   行间距 0.5rem（比面板的 0.9rem 块距紧一档），读起来是数字的注脚而不是另起一块。
   负外边距之类「把块距掰回来」的写法不必用，也就不引入新的间距常量 */
.stat-note {
  flex-basis: 100%;
  font-size: 12.5px;
  line-height: 1.7;
  opacity: 0.7;
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
  /* 颜色不写在这里：它由模板内联 `var(令牌名)` 给出，与折线共用同一个名字（见模板注释）。
     系列配色仍然是那四色两两可辨的组合——总资产取品牌强调色、净资产取转账蓝、
     收入取绿、支出取红（后两者的语义令牌本来就叫「收入 / 支出」，收款页与记账页已在用）。 */
}

/*
 * 绘图区：坐标轴刻度与日期都由 ECharts 画在 canvas 内，页面不再为它们留列，
 * 故这里只剩一个定高的盒子。高度与改动前一致（宽屏 240px、窄屏 180px），
 * 免得换库顺手改了整页的纵向节奏。
 */
.chart {
  height: 240px;
}

.head-actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 0.5rem;
}

/* 窄屏：图表降高，避免一屏只看得见曲线看不见别的 */
@media (max-width: 1023px) {
  .chart {
    height: 180px;
  }
}
</style>
