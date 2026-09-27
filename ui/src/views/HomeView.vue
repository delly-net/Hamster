<script setup lang="ts">
/**
 * `/` 首页（概览）：当前账套本月的**总资产走势**、**收支走势**两个图表面板，
 * 以及其下的**最近交易**只读列表。
 *
 * 两块图表数据都来自后端落库的**按天记录**（总资产结算订阅、收入结算订阅、支出结算订阅各自在结算
 * 事件后逐日重算），本页**不做任何金额汇总**——「哪些账户算资产、哪些算负债、个人与公共如何相加、
 * 收支的符号」全部由后端一处定义（见 `ITotalAssetSettlementService` / `IIncomeSettlementService` /
 * `IExpenseSettlementService`），前端再算一遍就会出现「首页的曲线与账户页、明细页对不上」
 * 这种谁也说不清的问题。图表部分只做一件事：把两条序列画出来。
 *
 * **两个面板共用一套几何与一套模板**：折线的坐标计算在 `components/lineChart.ts` 里，
 * 面板本身由 {@link panels} 描述后交给同一个 `v-for` 渲染。
 * 两份「长得一样但各写一遍」的 SVG 迟早会在留白、刻度或退化处理上分叉，
 * 而它们恰恰是必须逐字相同的部分（两个面板上下叠着，差一个像素都看得出来）。
 * 各面板**自己的**东西——标题、系列、概览数字、无障碍描述、空状态文案——仍各写各的
 * （在 {@link assetsPanel} / {@link flowsPanel} 里），那几处本来就该不同。
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
import {
  categoryText,
  counterpartyText,
  formatAmount,
  formatDateTime,
  formatSigned,
  signedCellClass,
  tagText,
} from '@/components/entryFormat'
import { buildLineChart, pickAxisLabels, VIEW_SIZE } from '@/components/lineChart'
import type { LineChartSeries, LineChartTick } from '@/components/lineChart'
import { useAccountSetsStore } from '@/stores/accountSets'
import { RECENT_ENTRY_LIMIT, transactionTypeLabel, useEntriesStore } from '@/stores/entries'
import { useIncomeExpensesStore } from '@/stores/incomeExpenses'
import type { IncomeExpenseDailyPoint } from '@/stores/incomeExpenses'
import { useTotalAssetsStore } from '@/stores/totalAssets'
import type { TotalAssetDailyPoint } from '@/stores/totalAssets'

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
  series: LineChartSeries[]
  /** 纵轴刻度：`y` 为逻辑坐标，`value` 为该处的金额。 */
  ticks: LineChartTick[]
  /** 概览数字（取自最新一个数据点）。 */
  stats: StatView[]
  /** 图例右侧的元信息（最新日期 · 天数 · 币种）。 */
  meta: string
  /** 横轴的三处标注（左端、中间、右端）；重复处为 `null` 以保持三列对齐。 */
  xLabels: (string | null)[]
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

/** 总资产面板：两条线共用一根纵轴（各自一根会让「净资产低于总资产」这个事实在图上消失）。 */
function assetsPanel(): Panel {
  const days = totalAssets.daily?.days ?? []
  const currencyLabel = totalAssets.daily?.currencyCode ?? ''
  const chart = buildLineChart<TotalAssetDailyPoint>(days, [
    // 总资产 = 资金账户合计（不含负债）；净资产 = 总资产 + 负债（负债带符号，故此处是加）
    { key: 'asset', label: '总资产', pick: (day) => day.assetTotal },
    { key: 'net', label: '净资产', pick: (day) => day.netTotal },
  ])
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
      chart === null || latest === undefined
        ? null
        : {
            series: chart.series,
            ticks: chart.ticks,
            stats: [
              { label: '总资产', value: formatAmount(latest.assetTotal) },
              { label: '净资产', value: formatAmount(latest.netTotal) },
              { label: '负债合计', value: formatAmount(latest.liabilityTotal) },
            ],
            meta: buildMeta(formatDay(latest.date), days.length, currencyLabel),
            xLabels: pickAxisLabels(days.map((day) => day.date)).map(formatDayOrNull),
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
 */
function flowsPanel(): Panel {
  const days = incomeExpenses.daily?.days ?? []
  const currencyLabel = incomeExpenses.daily?.currencyCode ?? ''
  const chart = buildLineChart<IncomeExpenseDailyPoint>(days, [
    { key: 'income', label: '收入', pick: (day) => day.incomeTotal },
    { key: 'expense', label: '支出', pick: (day) => day.expenseTotal },
  ])
  const latest = days[days.length - 1]

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
      chart === null || latest === undefined
        ? null
        : {
            series: chart.series,
            ticks: chart.ticks,
            stats: [
              { label: '收入', value: formatAmount(latest.incomeTotal) },
              { label: '支出', value: formatAmount(latest.expenseTotal) },
              { label: '净额', value: formatAmount(latest.netTotal) },
            ],
            meta: buildMeta(formatDay(latest.date), days.length, currencyLabel),
            xLabels: pickAxisLabels(days.map((day) => day.date)).map(formatDayOrNull),
            ariaLabel:
              `${period.value}收入与支出走势图，共 ${days.length} 个数据点。` +
              `最新一天（${formatDay(latest.date)}）收入 ${formatAmount(latest.incomeTotal)}、` +
              `支出 ${formatAmount(latest.expenseTotal)}、` +
              `净额 ${formatAmount(latest.netTotal)}` +
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

/** 横轴标注用：`null` 仍为 `null`（那是「这个位置不标」而不是一个日期）。 */
function formatDayOrNull(date: string | null): string | null {
  return date === null ? null : formatDay(date)
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
          <div class="stats">
            <div v-for="stat in panel.chart.stats" :key="stat.label" class="stat">
              <span class="stat-label">{{ stat.label }}</span>
              <strong class="stat-value">{{ stat.value }}</strong>
            </div>
          </div>

          <div class="legend">
            <span v-for="item in panel.chart.series" :key="item.key" class="legend-item">
              <i class="swatch" :class="`swatch-${item.key}`" />
              {{ item.label }}
            </span>
            <span class="legend-meta">{{ panel.chart.meta }}</span>
          </div>

          <div class="chart">
            <div class="y-axis">
              <span v-for="tick in panel.chart.ticks" :key="tick.y">
                {{ formatAmount(tick.value) }}
              </span>
            </div>

            <svg
              class="plot"
              :viewBox="`0 0 ${VIEW_SIZE} ${VIEW_SIZE}`"
              preserveAspectRatio="none"
              role="img"
              :aria-label="panel.chart.ariaLabel"
            >
              <line
                v-for="tick in panel.chart.ticks"
                :key="tick.y"
                class="grid"
                x1="0"
                :y1="tick.y"
                :x2="VIEW_SIZE"
                :y2="tick.y"
              />
              <path
                v-for="item in panel.chart.series"
                :key="item.key"
                class="line"
                :class="`line-${item.key}`"
                :d="item.path"
                vector-effect="non-scaling-stroke"
              />
            </svg>

            <div class="x-axis">
              <span v-for="(label, index) in panel.chart.xLabels" :key="index">
                {{ label ?? '' }}
              </span>
            </div>
          </div>
        </template>
      </section>

      <!--
        最近交易：**排在两张走势图之后**——本页的身份是「走势概览」，明细是补充而不是主体。
        整块**只读**：不给改账入口、不引入弹窗，也不重复明细页的筛选与分页，
        要看全部或要改账都走标题行右侧那个链接。
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
        <p v-else-if="entries.recentItems.length === 0" class="hint">
          当前账套还没有账目明细。
        </p>

        <ul v-else class="recent">
          <li v-for="entry in entries.recentItems" :key="entry.id" class="recent-row">
            <div class="recent-main">
              <p class="recent-line">
                <span class="recent-summary">{{ entry.summary }}</span>
                <!-- 交易类型是必需的那一个标签：它与金额不同，「转账」正是用户区分
                     「账户之间的搬运」与「真正的收支」的唯一线索。收入/支出则不再用文字重复
                     ——金额已经带符号又着了色 -->
                <span class="recent-type">{{ transactionTypeLabel(entry.transactionType) }}</span>
              </p>
              <!-- 账户 → 对手方：「钱从哪来、到哪去」的对照（与明细页窄屏卡片的排法同构） -->
              <p class="recent-line recent-path">
                <span class="recent-account">{{ entry.accountName }}</span>
                <span class="recent-arrow" aria-hidden="true">→</span>
                <span class="recent-counterparty">{{ counterpartyText(entry) }}</span>
              </p>
              <!-- 时间 / 分类 / 标签：每行都要读得到、又不抢金额视线。**不含备注**：
                   备注是最长的一段自由文本，10 行会让面板失控，要看它去明细页 -->
              <p class="recent-line recent-meta">
                <span class="recent-time">{{ formatDateTime(entry.occurredAt) }}</span>
                <span>分类：{{ categoryText(entry) }}</span>
                <span>标签：{{ tagText(entry) }}</span>
              </p>
            </div>
            <!-- 金额是这一行的视觉主位：带符号（`+` / `-`）并按正负着色，
                 两位小数与明细页共用同一份格式化实现；字段缺失时显示【金额异常】而不是 NaN -->
            <span class="recent-amount" :class="signedCellClass(entry.signedAmount, 'net')">
              {{ formatSigned(entry.signedAmount) }}
            </span>
          </li>
        </ul>
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
 * 最近交易列表：**只读**，整块没有任何可点元素，故行与行之间用一条分隔线而不是各自成卡
 * ——十张卡片会把首页撑得很长，而它们承载的信息量只够一行。
 */
.recent {
  display: flex;
  flex-direction: column;
  margin: 0;
  padding: 0;
  list-style: none;
}

.recent-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.55rem 0;
  border-top: 1px solid var(--color-border);
}

/* 首行不画分隔线：它紧跟在说明文字之后，再画一条会把说明与列表切开 */
.recent-row:first-child {
  border-top: none;
  padding-top: 0;
}

/* 左栏：三行文字。`min-width: 0` 让它在窄屏下可以让位给右侧金额，而不是把金额挤出容器 */
.recent-main {
  display: flex;
  flex: 1 1 auto;
  flex-direction: column;
  gap: 0.15rem;
  min-width: 0;
}

/* 一行内的若干片段：允许折行，故长账户名/长摘要在窄屏下换行而不是横向溢出 */
.recent-line {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0.2rem 0.5rem;
  min-width: 0;
}

/* 摘要允许换行、不截断：它是这一行唯一的自由文本，截断等于丢信息（同明细页卡片） */
.recent-summary {
  font-size: 13.5px;
  overflow-wrap: anywhere;
}

/* 交易类型弱化到与明细页同一档（12px / 0.6）：它是背景信息，不抢摘要的视觉重心 */
.recent-type {
  font-size: 12px;
  opacity: 0.6;
  white-space: nowrap;
}

/* 「账户 → 对手方」：账户名占弹性空间，空间不足时截断，不去挤右侧的对手方 */
.recent-path {
  font-size: 13px;
  font-weight: 600;
}

.recent-account {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.recent-arrow,
.recent-counterparty {
  flex: none;
}

/* 时间 / 分类 / 标签：同为「每行都要读得到、又都不抢金额视线」的次要信息，排一行（同明细页卡片） */
.recent-meta {
  font-size: 12.5px;
  line-height: 1.6;
  opacity: 0.75;
}

.recent-time {
  font-variant-numeric: tabular-nums;
}

/* 金额：这一行的视觉主位（字号略大于正文，但不与图表面板那三个概览数字抢） */
.recent-amount {
  flex: none;
  font-size: 15px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

/* 收支语义色：正数绿（收入侧）、负数红（支出侧），与明细页共用同一批语义令牌——全站只有一个红。
   类名由 `entryFormat.signedCellClass` 给出，故两份样式定义必须同名同义。 */
.income {
  color: var(--color-income);
}

.expense {
  color: var(--color-expense);
}

/* 金额字段缺失：复用危险色，但语义与 .expense 完全不同——红色在这里说的是
   「这个值不可信」，不是「这是一笔支出」 */
.amount-unknown {
  color: var(--color-danger);
}

/*
 * 面板内的状态文案（加载中 / 暂无数据 / 失败）：
 * 卡片自己已经有描边与底色，这两行再各套一层框会变成「框里套框」，
 * 故在此把外边距与底色让掉，只保留文字本身。
 */
.panel > .hint,
.panel > .error {
  padding: 0.15rem 0;
  border: none;
  border-radius: 0;
  background: none;
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

/*
 * 系列配色只用语义令牌，四色两两可辨：
 * 总资产取品牌强调色、净资产取转账蓝、收入取绿、支出取红——
 * 后两者的语义令牌本来就叫「收入 / 支出」（收款页、记账页已在用），此处沿用同一个词、不另配色。
 */
.swatch-asset {
  background-color: var(--color-accent);
}

.swatch-net {
  background-color: var(--color-transfer);
}

.swatch-income {
  background-color: var(--color-income);
}

.swatch-expense {
  background-color: var(--color-expense);
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

.line-income {
  stroke: var(--color-income);
}

.line-expense {
  stroke: var(--color-expense);
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
