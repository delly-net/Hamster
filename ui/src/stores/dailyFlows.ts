/**
 * 按日收支状态：当前用户在当前账套里某个月**每天**的收入与支出，供月报表（`/reports`）的折线图使用。
 *
 * 数据来自 `GET /api/entries/daily-flows`，由后端**实时聚合交易明细**得出，
 * 与首页的「收支」面板（`stores/incomeExpenses.ts`，读数来自结算订阅落库的按天记录）**不是同一条数据源**：
 * 结算记录只含已经结算的日期，而月报表要看到本月截止今天的每一笔收支。
 * 两者口径（默认币种、公共 + 本人个人、资金/负债账户、只计收入与支出）相同，差别只在「实时 / 已结算」。
 *
 * **本 store 不做任何金额汇总与补零**：后端如实给出「哪些天真的发生过收支」，前端只负责把整月
 * 缺的日期补成 0 去画图（见 `views/ReportView.vue`）——补零是呈现层的决定，不是聚合口径。
 *
 * 请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，故**本 store 不做账套判断**——
 * 切换账套后的重新查询由页面 watch 当前账套负责（与 `stores/incomeExpenses.ts` 同构）。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/** 按天收支的一个数据点；字段口径与后端 `EntryDailyFlowDto` 一一对应。 */
export interface DailyFlowPoint {
  /** 日期（`yyyy-MM-dd`，**服务器本地日期**）。 */
  date: string
  /** 当日收入合计，**恒为非负**。 */
  incomeTotal: number
  /** 当日支出合计，**恒为非负**。 */
  expenseTotal: number
  /** 净额 = `incomeTotal - expenseTotal`（**带符号**，花超时为负）。 */
  netTotal: number
}

/** 某月的按天收支。 */
export interface DailyFlows {
  /**
   * 金额所属的币种代码；**一个启用币种都没有时为 `null`**（此时 `days` 必为空）。
   *
   * 由后端下发而不是前端自己查：只有后端知道它统计的是哪一组账户。
   */
  currencyCode: string | null
  /** 实际查询的月份（`yyyy-MM`）；未指定月份时即服务器本地的当月。 */
  month: string
  /**
   * 按日期升序的数据点；**只含至少有一笔收支的日期**，没有时是空数组。
   *
   * 后端**不补零**：哪些天真的发生过收支是账本事实，而「整月折线要不要把空白日画成 0」
   * 属于呈现层——月报表的数据源是实时的，空白日就是真实的 0（与结算图表的「缺日不补零」不同）。
   */
  days: DailyFlowPoint[]
}

/** 接口基址。 */
const DAILY_FLOWS_PATH = '/api/entries/daily-flows'

export const useDailyFlowsStore = defineStore('dailyFlows', () => {
  /** 最近一次查询结果；`null` 表示尚未查过。 */
  const daily = ref<DailyFlows | null>(null)
  const loading = ref(false)

  /**
   * 读取某个月的按天收支。
   *
   * @param month 目标月份（`yyyy-MM`）；**省略时由后端取服务器本地的当月**——日期的分组
   *   口径跟着服务器走，前端按浏览器时区算一个月，跨时区部署时就会差一天。
   * @returns 该月的按天收支（没有任何收支时 `days` 为空数组）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadDaily(month?: string): Promise<DailyFlows> {
    const search = month === undefined ? '' : `?month=${encodeURIComponent(month)}`

    loading.value = true
    try {
      const result = await request<DailyFlows>(`${DAILY_FLOWS_PATH}${search}`)
      daily.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /** 清空结果（退出登录、账套切换时调用，避免残留上一账套的曲线）。 */
  function clear(): void {
    daily.value = null
  }

  return {
    daily,
    loading,
    loadDaily,
    clear,
  }
})
