/**
 * 收支状态：当前用户在当前账套里某个月的按天收入与支出，供首页的当月收支图使用。
 *
 * 数据来自**后端落库的按天记录**（收入结算订阅与支出结算订阅在结算事件后各自逐日重算，
 * 见后端 `IIncomeSettlementService` / `IExpenseSettlementService`），**不是前端实时汇总**：
 * 金额的口径（哪些账户算收入、个人与公共如何相加、金额的符号）全在后端一处定义，
 * 前端只做呈现——在这里再算一遍，就会出现「首页的曲线与明细页对不上」这种谁也说不清的问题。
 *
 * 请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，故**本 store 不做账套判断**——
 * 切换账套后的重新查询由页面 watch 当前账套负责（与 `stores/totalAssets.ts` 同构）。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/** 按天收支的一个数据点；字段口径与后端 `IncomeExpenseDailyPoint` 一一对应。 */
export interface IncomeExpenseDailyPoint {
  /** 日期（`yyyy-MM-dd`，**本地日期**）。 */
  date: string
  /** 收入合计 = 个人的收入 + 公共的收入，**恒为非负**。 */
  incomeTotal: number
  /** 支出合计 = 个人的支出 + 公共的支出，**恒为非负**（前端的「支出线」也照此画在正半轴）。 */
  expenseTotal: number
  /** 净额 = `incomeTotal - expenseTotal`（**带符号**，花超时为负）。 */
  netTotal: number
}

/** 某月的按天收支。 */
export interface IncomeExpenseDaily {
  /**
   * 金额所属的币种代码；**一个启用币种都没有时为 `null`**（此时 `days` 必为空）。
   *
   * 由后端下发而不是前端自己查：记录按币种分行，只有后端知道它取的是哪一组。
   */
  currencyCode: string | null
  /** 实际查询的月份（`yyyy-MM`）；未指定月份时即服务器本地的当月。 */
  month: string
  /**
   * 按日期升序的数据点；**没有记录时是空数组**。
   *
   * 只含**至少一侧落过库的日子**（收入表与支出表日期的**并集**）：两个订阅各推各的水位，
   * 短暂的不同步是可能的，此时缺的那一侧按 0 呈现；两侧都没有记录的日子不出现——
   * 补零会把「这天还没结算」画成「这天收支为 0」，图上是一根掉到底的线，那是错误信息而不是缺失信息。
   */
  days: IncomeExpenseDailyPoint[]
}

/** 接口基址。 */
const INCOME_EXPENSES_PATH = '/api/income-expenses'

export const useIncomeExpensesStore = defineStore('incomeExpenses', () => {
  /** 最近一次查询结果；`null` 表示尚未查过。 */
  const daily = ref<IncomeExpenseDaily | null>(null)
  const loading = ref(false)

  /**
   * 读取某个月的按天收支。
   *
   * @param month 目标月份（`yyyy-MM`）；**省略时由后端取服务器本地的当月**——记录的日期是
   * 本地日期，跟着服务器走才不会与落库的日期错位。
   * @returns 该月的收支序列（未结算时 `days` 为空数组）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadDaily(month?: string): Promise<IncomeExpenseDaily> {
    const search = month === undefined ? '' : `?month=${encodeURIComponent(month)}`

    loading.value = true
    try {
      const result = await request<IncomeExpenseDaily>(`${INCOME_EXPENSES_PATH}/daily${search}`)
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
