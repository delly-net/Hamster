/**
 * 总资产状态：当前用户在当前账套里某个月的按天总资产，供首页走势图使用。
 *
 * 数据来自**后端落库的按天记录**（总资产结算订阅在结算事件后逐日重算，见后端
 * `ITotalAssetSettlementService`），**不是前端实时汇总**：金额的口径（哪些账户算资产、哪些算负债、
 * 个人与公共如何相加、负债的符号）全在后端一处定义，前端只做呈现——在这里再算一遍，
 * 就会出现「首页的曲线与账户页的余额对不上」这种谁也说不清的问题。
 *
 * 请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，故**本 store 不做账套判断**——
 * 切换账套后的重新查询由页面 watch 当前账套负责（与 `stores/entries.ts` 同构）。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/** 按天总资产的一个数据点；字段口径与后端 `TotalAssetDailyPoint` 一一对应。 */
export interface TotalAssetDailyPoint {
  /** 日期（`yyyy-MM-dd`，**本地日期**）。 */
  date: string
  /** 资产合计 = 个人的资金账户 + 公共的资金账户。 */
  assetTotal: number
  /** 负债合计 = 个人的负债账户 + 公共的负债账户，**带符号**（欠款为负）。 */
  liabilityTotal: number
  /** 净资产 = `assetTotal + liabilityTotal`（负债本身为负，故此处是加）。 */
  netTotal: number
}

/** 某月的按天总资产。 */
export interface TotalAssetDaily {
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
   * 只含**确实落过库的日子**（已结算的那些天），缺日不补零：补零会把「这天还没结算」
   * 画成「这天资产为 0」，图上是一根掉到底的线，那是错误信息而不是缺失信息。
   */
  days: TotalAssetDailyPoint[]
}

/** 接口基址。 */
const TOTAL_ASSETS_PATH = '/api/total-assets'

export const useTotalAssetsStore = defineStore('totalAssets', () => {
  /** 最近一次查询结果；`null` 表示尚未查过。 */
  const daily = ref<TotalAssetDaily | null>(null)
  const loading = ref(false)

  /**
   * 读取某个月的按天总资产。
   *
   * @param month 目标月份（`yyyy-MM`）；**省略时由后端取服务器本地的当月**——记录的日期是
   * 本地日期，跟着服务器走才不会与落库的日期错位。
   * @returns 该月的总资产序列（未结算时 `days` 为空数组）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadDaily(month?: string): Promise<TotalAssetDaily> {
    const search = month === undefined ? '' : `?month=${encodeURIComponent(month)}`

    loading.value = true
    try {
      const result = await request<TotalAssetDaily>(`${TOTAL_ASSETS_PATH}/daily${search}`)
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
