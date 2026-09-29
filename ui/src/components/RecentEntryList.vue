<script setup lang="ts">
/**
 * 只读的「最近交易」行列表：**一处定义、两页共用**。
 *
 * 两个调用方要的是同一份东西——按发生时间倒序的若干条明细，一行一件事，摘要 / 账户 → 对手方 /
 * 时间 · 分类 · 标签 / 金额：
 * - 首页（`views/HomeView.vue`）的「最近交易」面板：混排全部类型，取最新 10 条**明细行**；
 * - 记账页（`components/EntryRecordForm.vue`）的「最近 5 次同类型交易」：只含本页那一种类型，
 *   且已按笔归并（一行一笔，见 `stores/entries.ts` 的 `mergeToTransactions`）。
 *
 * 行的模板与样式若在两处各写一份，就会像 `entryFormat.ts` 文件头警告的那样「抄一份等于立了第二个真源」：
 * 金额的符号与语义色、`Ledger` 档读作「账本」而不是「期初」、未分类的 `—`，这些都是已经确认过的
 * 全站口径，分头维护迟早漂移出「同一笔账在两页读起来不一样」。故此处收成一处。
 *
 * **本组件是纯呈现、无状态**：不取数、不持有错误或加载态——那是各页自己的事（两页打的是同一个端点，
 * 但参数、条数与失败归属都不同）。组件的根是**片段**（`v-if` / `v-else` 两个顶层节点、没有包裹元素）：
 * 渲染出来的 DOM 与原先写在各页里时完全相同，多出来的只是一个组件边界。
 * **但别指望各页按位置命中的规则能穿透进来**——片段根拿不到父组件的 scoped 标记，
 * `.panel > .hint` 那类选择器对这里渲染的节点不生效（`:deep()` 是唯一出路，本组件不走那条路：
 * 空态的文字样式收在本组件里，各页只管自己那两条状态文案）。
 *
 * **两处唯一的差异是 `showType`**：首页混排三种类型，交易类型标签是用户区分「账户之间的搬运」
 * 与「真正的收支」的唯一线索，必需；记账页整块只有一种类型，标签冗余（面板标题已经写明）。
 */
import {
  categoryText,
  counterpartyText,
  formatDateTime,
  formatSigned,
  signedCellClass,
  tagText,
} from '@/components/entryFormat'
import { transactionTypeLabel, type Entry } from '@/stores/entries'

const props = defineProps<{
  /** 要呈现的明细行；为空数组时呈现 `emptyText`。 */
  entries: Entry[]
  /** 无数据时的提示文案。**由调用方给定**：首页说的是「还没有账目明细」，记账页说的是「还没有收入」。 */
  emptyText: string
  /** 是否在每行呈现交易类型标签；默认不呈现（见文件头）。 */
  showType?: boolean
}>()
</script>

<template>
  <!-- 空态：文案由调用方给（见 props 说明），此处只管不套双框——外层卡片已经有描边与底色时，
       调用方自己的 `.panel > .hint` 规则会把这层框再让掉 -->
  <p v-if="props.entries.length === 0" class="hint">{{ props.emptyText }}</p>

  <ul v-else class="recent">
    <li v-for="entry in props.entries" :key="entry.id" class="recent-row">
      <div class="recent-main">
        <p class="recent-line">
          <span class="recent-summary">{{ entry.summary }}</span>
          <!-- 交易类型是必需的那一个标签：它与金额不同，「转账」正是用户区分
               「账户之间的搬运」与「真正的收支」的唯一线索。收入/支出则不再用文字重复
               ——金额已经带符号又着了色 -->
          <span v-if="props.showType" class="recent-type">
            {{ transactionTypeLabel(entry.transactionType) }}
          </span>
        </p>
        <!-- 账户 → 对手方：「钱从哪来、到哪去」的对照（与明细页窄屏卡片的排法同构） -->
        <p class="recent-line recent-path">
          <span class="recent-account">{{ entry.accountName }}</span>
          <span class="recent-arrow" aria-hidden="true">→</span>
          <span class="recent-counterparty">{{ counterpartyText(entry) }}</span>
        </p>
        <!-- 时间 / 分类 / 标签：每行都要读得到、又不抢金额视线。**不含备注**：
             备注是最长的一段自由文本，十行会让面板失控，要看它去明细页 -->
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
</template>

<style scoped>
/*
 * 空态：与各页的「加载中…」同一档文字（13px / 1.7 / 0.8），但**自身不套框**。
 *
 * 两个调用方都是把本组件放进一张已有描边与底色的卡片里（首页的「最近交易」面板、
 * 记账页的「最近 N 次」面板），再套一层框就是「框里套框」。各页因此才能像
 * `.panel > .hint` 那样只让掉自己那两条状态文案的框，而不必去穿透子组件
 * （**本组件的根是片段**，父组件的 scoped 样式不会落到它渲染出的节点上，`:deep()` 是唯一出路）。
 */
.hint {
  padding: 0.15rem 0;
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.8;
}

/*
 * 列表：**只读**，整块没有任何可点元素，故行与行之间用一条分隔线而不是各自成卡
 * ——十张卡片会把页面撑得很长，而它们承载的信息量只够一行。
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

/* 一行内的若干片段：允许折行，故长账户名/长摘要在窄屏下换行而不是横向溢出。
   全站唯一的断点仍是 App.vue 的 1023px，页内不再另设媒体查询 */
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
   类名由 `entryFormat.signedCellClass` 给出，故各处样式定义必须同名同义 */
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
</style>
