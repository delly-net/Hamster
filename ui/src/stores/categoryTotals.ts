/**
 * 收支分类合计状态：当前用户在当前账套里某个月的收入 / 支出按分类的合计，
 * 供首页的「收入构成 / 支出构成」百分比饼图使用。
 *
 * 数据来自 `GET /api/entries/category-totals`，由**后端一次 GROUP BY 聚合**：
 * `GET /api/entries` 的行粒度是明细且单页最多 200 条，拿它在前端累加整月既搬载荷又只能覆盖
 * 最近一页；分类占比要的是「整月每类各多少」，那是聚合形状，不是明细页的另一种参数。
 *
 * **本 store 不做任何金额汇总与分类折叠**：后端给出「分类 → 金额」，前端只负责把金额折成
 * 百分比与最多 6 个扇区（见 `components/categoryPie.ts`）。可见性、币种、交易类型与月份边界
 * 全在后端一处定义——在这里再筛一遍，只会造出与后端不一致的假防线。
 *
 * 请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，故**本 store 不做账套判断**——
 * 切换账套后的重新查询由页面 watch 当前账套负责（与 `stores/incomeExpenses.ts` 同构）。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/**
 * 某个分类在本月的收入或支出合计。
 *
 * 字段口径与后端 `EntryCategoryTotalDto` 一一对应。
 */
export interface EntryCategoryTotal {
  /** 分类主键；**未分类时为 `null`**（合法状态，不是数据缺失）。 */
  categoryId: number | null
  /** 分类名称；未分类时为 `null`，与 `categoryId` 同生同灭；已停用分类的名称照常给出。 */
  categoryName: string | null
  /** 本月发生额，**恒为正**；方向由它所在的 `income` / `expense` 列表表达。 */
  amount: number
}

/** 某月的收支分类合计。 */
export interface EntryCategoryTotals {
  /**
   * 金额所属的币种代码；**一个启用币种都没有时为 `null`**（此时两个列表必为空）。
   *
   * 由后端下发而不是前端自己查：只有后端知道它统计的是哪一组账户。
   */
  currencyCode: string | null
  /** 实际查询的月份（`yyyy-MM`）；未指定月份时即服务器本地的当月。 */
  month: string
  /** 收入侧的分类合计，按金额降序；没有收入发生时是空数组。 */
  income: EntryCategoryTotal[]
  /** 支出侧的分类合计，按金额降序；没有支出发生时是空数组。 */
  expense: EntryCategoryTotal[]
}

/** 接口基址。 */
const CATEGORY_TOTALS_PATH = '/api/entries/category-totals'

export const useCategoryTotalsStore = defineStore('categoryTotals', () => {
  /** 最近一次查询结果；`null` 表示尚未查过。 */
  const totals = ref<EntryCategoryTotals | null>(null)
  const loading = ref(false)

  /**
   * 读取某个月的收支分类合计。
   *
   * @param month 目标月份（`yyyy-MM`）；**省略时由后端取服务器本地的当月**——交易日期的分组
   *   口径跟着服务器走，前端按浏览器时区算一个月，跨时区部署时就会差一天。
   * @returns 该月的分类合计（没有任何收入与支出时两个列表都是空数组）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadTotals(month?: string): Promise<EntryCategoryTotals> {
    const search = month === undefined ? '' : `?month=${encodeURIComponent(month)}`

    loading.value = true
    try {
      const result = await request<EntryCategoryTotals>(`${CATEGORY_TOTALS_PATH}${search}`)
      totals.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /** 清空结果（退出登录、账套切换时调用，避免残留上一账套的饼图）。 */
  function clear(): void {
    totals.value = null
  }

  return {
    totals,
    loading,
    loadTotals,
    clear,
  }
})
