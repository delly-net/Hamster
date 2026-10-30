/**
 * 月报表状态：当前用户在当前账套里**任意月份区间**（可按账户、标签收窄）的三份聚合，
 * 供月报表页（`/reports`）的饼图、折线与表格使用。
 *
 * 数据来自 `GET /api/entries/monthly-report`，由后端**实时聚合交易明细**得出，
 * 与首页的「收支」面板（读数来自结算订阅落库的按天记录）**不是同一条数据源**：
 * 结算记录只含已经结算的日期，而月报表要看到截止今天的每一笔收支。
 * 口径（默认币种、公共 + 本人个人、资金 / 负债账户、只计收入与支出）与
 * `daily-report` 逐条相同，差异只在时间粒度为**月**、且区间可任意指定。
 *
 * **三份结果来自同一个请求**：后端一次取回候选行，在内存里同时折出按月、按分类与按标签。
 * 本 store 因此不做任何跨块拼装，也不做金额汇总与补零——补零是呈现层的决定
 * （见 `views/ReportView.vue`），聚合口径全在后端一处定义。
 *
 * 请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，故**本 store 不做账套判断**——
 * 切换账套后的重新查询由页面 watch 当前账套负责（与 `stores/dailyReport.ts` 同构）。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'
import type { EntryCategoryTotal } from '@/stores/categoryTotals'
import type { ReportSideTotals, ReportTagTotal } from '@/stores/dailyReport'

/** 某个月在区间内的收入、支出与净额；字段口径与后端 `EntryMonthlyFlowDto` 一一对应。 */
export interface MonthlyFlowPoint {
  /** 月份（`yyyy-MM`，**服务器本地月份**）。 */
  month: string
  /** 当月收入合计，恒为非负。 */
  incomeTotal: number
  /** 当月支出合计，恒为非负。 */
  expenseTotal: number
  /** 净额 = 收入 − 支出（带符号）。 */
  netTotal: number
}

/** 月报表的完整结果。 */
export interface MonthlyReport {
  /**
   * 金额所属的币种代码；**一个启用币种都没有时为 `null`**（此时三份集合必为空）。
   *
   * 由后端下发而不是前端自己查：只有后端知道它统计的是哪一组账户。
   */
  currencyCode: string | null
  /** 实际查询的起始月份（`yyyy-MM`，**服务器本地月份**，含）。 */
  from: string
  /** 实际查询的结束月份（`yyyy-MM`，**服务器本地月份**，含）。 */
  to: string
  /**
   * 按月份升序的按月收支；**只含至少有一笔收支的月份**（后端不补零）。
   *
   * 补零由页面完成：月报表的数据源是实时的，空白月份就是真实的 0
   * （与首页结算图表的「缺日不补零」刻意不同）。
   */
  months: MonthlyFlowPoint[]
  /**
   * 按分类的收入与支出合计。
   *
   * 复用 `stores/categoryTotals.ts` 的 `EntryCategoryTotal` 形状而不是另定义一份：
   * 两者都是「一个可空主键 + 名称 + 金额」，且共用同一张饼图组件。
   */
  categories: ReportSideTotals<EntryCategoryTotal>
  /** 按标签的收入与支出合计；形状与日报表同一份定义。 */
  tags: ReportSideTotals<ReportTagTotal>
}

/** 一次月报表查询的条件。 */
export interface MonthlyReportQuery {
  /** 起始月份（`yyyy-MM`）；省略时后端取当前月往前推 11 个月。 */
  from?: string
  /** 结束月份（`yyyy-MM`）；省略时后端取服务器本地当前月。 */
  to?: string
  /** 账户主键；空数组或省略即不限账户（后端仍只统计可见的默认币种钱账户）。 */
  accountIds?: readonly number[]
  /** 标签主键；空数组或省略即不限标签，非空时**任一命中**。 */
  tagIds?: readonly number[]
}

/** 接口基址。 */
const MONTHLY_REPORT_PATH = '/api/entries/monthly-report'

export const useMonthlyReportStore = defineStore('monthlyReport', () => {
  /** 最近一次查询结果；`null` 表示尚未查过。 */
  const report = ref<MonthlyReport | null>(null)
  const loading = ref(false)

  /**
   * 读取一段月份区间的月报表聚合。
   *
   * @param query 月份区间与账户 / 标签筛选；全部省略时后端取「当前月往前推 11 个月 ~ 当前月」。
   * @returns 该区间的三份聚合（没有任何收支时 `months` 与两侧列表都是空数组）。
   * @throws 未选择账套时后端返回 400；月份非法、区间倒置或超过 36 个月返回 400；
   *   令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadReport(query: MonthlyReportQuery = {}): Promise<MonthlyReport> {
    const search = new URLSearchParams()
    if (query.from !== undefined && query.from !== '') {
      search.set('from', query.from)
    }
    if (query.to !== undefined && query.to !== '') {
      search.set('to', query.to)
    }
    // 多个主键逐项 append：后端的 int[]? 绑定按重复查询参数取值，逗号串不会被拆开
    for (const id of query.accountIds ?? []) {
      search.append('accountIds', String(id))
    }
    for (const id of query.tagIds ?? []) {
      search.append('tagIds', String(id))
    }

    const suffix = search.toString()
    loading.value = true
    try {
      const result = await request<MonthlyReport>(
        suffix === '' ? MONTHLY_REPORT_PATH : `${MONTHLY_REPORT_PATH}?${suffix}`,
      )
      report.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /** 清空结果（退出登录、账套切换、发新请求前调用，避免残留上一份数据的画面）。 */
  function clear(): void {
    report.value = null
  }

  return {
    report,
    loading,
    loadReport,
    clear,
  }
})
