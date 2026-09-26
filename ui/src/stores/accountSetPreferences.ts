/**
 * 个人账套配置：当前用户在当前账套里的界面设置，目前只有账目明细页的筛选条件。
 *
 * 请求由 `api/http.ts` 自动附带 `X-Account-Set-Id`，故**本 store 不做账套判断**——
 * 「谁在哪个账套里存了什么」由后端按「账套 + 用户」唯一地判定，前端只需如实收发。
 *
 * **本 store 刻意不缓存读到的配置、也没有 `loading`**：它与其余 store 不同，装的是一个**状态的来源**，
 * 而消费方（账目明细页）自己就持有那份草稿。在 store 里再存一份会多出一个真值源——
 * 账套切换后忘了刷新它，就会把上一账套的筛选条件恢复到这一账套上，而这正是本功能最容易出的错。
 * 一次性读、一次性写，页面拿到什么就是什么。
 */

import { defineStore } from 'pinia'
import { request } from '@/api/http'

/**
 * 账目明细页的筛选条件。
 *
 * **两个数组恒有值**（没保存过时后端回的是两个空数组而不是 `null`），消费方直接遍历即可。
 * 空 `accountIds` 是「没保存过账户条件」，空 `tagIds` 是「不限标签」——两者的处置相同（都回落默认）。
 *
 * **不含日期区间**：时间区间每次进入页面都回到默认的「本月 1 日~今天」，
 * 这是刻意的取舍（日期是「我这次想看哪一段」，不是「我习惯怎么看账」）。
 */
export interface EntryFilterPreference {
  /** 账目明细页选中的账户主键。 */
  accountIds: number[]
  /** 账目明细页选中的标签主键；空数组即不限标签。 */
  tagIds: number[]
}

/** 接口基址。 */
const PREFERENCES_PATH = '/api/account-set-preferences'

export const useAccountSetPreferencesStore = defineStore('accountSetPreferences', () => {
  /**
   * 读取当前用户在当前账套里保存的账目明细页筛选条件。
   *
   * @returns 保存过的条件；**从未保存过时返回两个空数组**（调用方据此回落到默认视图）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function loadEntryFilter(): Promise<EntryFilterPreference> {
    return await request<EntryFilterPreference>(`${PREFERENCES_PATH}/entry-filter`)
  }

  /**
   * 保存当前用户在当前账套里的账目明细页筛选条件（后端按「账套 + 用户」upsert，不会追加第二行）。
   *
   * **不属于当前账套的主键由后端静默丢弃**，本方法不为此做前置校验：前端手上的主键本就来自
   * 当前账套的候选列表，再判一次只是把同一条判据写成两份。
   */
  async function saveEntryFilter(payload: EntryFilterPreference): Promise<void> {
    await request<void>(`${PREFERENCES_PATH}/entry-filter`, { method: 'PUT', body: payload })
  }

  return {
    loadEntryFilter,
    saveEntryFilter,
  }
})
