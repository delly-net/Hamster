/**
 * 分类状态。
 *
 * 分类归属且仅归属一个账套，列表按**当前账套**过滤：请求由 `api/http.ts` 自动附带
 * `X-Account-Set-Id`，此处不做账套判断——切换账套后的重新拉取由页面 watch 当前账套负责。
 *
 * 与账户不同，分类**没有可见性维度**：账套内所有成员看到的是同一份完整字典，
 * 也没有「他人私有的分类」这一概念。维护它不需要管理员身份（这与币种刻意相反：
 * 币种是全局字典，改动影响所有账套，故维护能力收在管理端）。
 *
 * 分类**不区分记账类型**：同一份字典供收入/支出/转账三类共用，本模块因此没有类型字段。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/** 分类（后端不回传内部明细）。 */
export interface Category {
  id: number
  accountSetId: number
  /** 分类名称；交易写入时上报的 `categoryId` 即 {@link id}。 */
  name: string
  /** 是否启用；`false` 表示已停用（软删除）。 */
  isActive: boolean
  /** 创建时间（UTC，ISO 8601）。 */
  createdAt: string
}

/** 新建分类的入参。 */
export interface CreateCategoryPayload {
  name: string
}

/**
 * 修改分类的入参。
 *
 * **只有名称**：所属账套一经创建不可修改——改归属等于把这个分类从一家的字典搬到另一家，
 * 而挂在它上面的历史流水并不跟着搬家。后端 `PUT` 的请求体同样只有名称。
 */
export interface UpdateCategoryPayload {
  name: string
}

/** 接口基址。 */
const CATEGORIES_PATH = '/api/categories'

export const useCategoriesStore = defineStore('categories', () => {
  /** 当前账套内的分类。 */
  const categories = ref<Category[]>([])
  const loading = ref(false)

  /**
   * 拉取当前账套内的分类。
   *
   * @param includeInactive 是否包含已停用的分类；记账表单用默认的 `false`（停用的不该再被选），
   * 分类管理页用 `true`（要能看见并重新启用它们）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function list(includeInactive = false): Promise<Category[]> {
    loading.value = true
    try {
      const result = await request<Category[]>(
        `${CATEGORIES_PATH}?includeInactive=${includeInactive}`,
      )
      categories.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /**
   * 新建分类；名称重复时后端返回 409。
   *
   * 记账表单**不需要**先调本方法：后端在收到候选之外的分类名时会自动创建它。
   * 本方法是分类管理页「事先建好一份字典」的入口。
   */
  async function create(payload: CreateCategoryPayload): Promise<void> {
    await request<Category>(CATEGORIES_PATH, { method: 'POST', body: payload })
  }

  /** 修改分类名称；改名后历史明细自动显示新名字（流水挂的是主键，不是名称）。 */
  async function update(id: number, payload: UpdateCategoryPayload): Promise<void> {
    await request<void>(`${CATEGORIES_PATH}/${id}`, { method: 'PUT', body: payload })
  }

  /** 启用或停用分类（停用即软删除，数据行保留）。 */
  async function setActive(id: number, isActive: boolean): Promise<void> {
    await request<void>(`${CATEGORIES_PATH}/${id}/${isActive ? 'activate' : 'deactivate'}`, {
      method: 'POST',
    })
  }

  /**
   * 清空列表。
   *
   * 两个调用方、同一件事——**手上的这份分类字典已不再是事实**：
   * 1. 退出登录或账套失效（避免残留上一账套的分类）；
   * 2. **记账提交成功后**（见 `EntryRecordForm.vue`）：记账人打出一个候选里没有的分类名时，
   *    后端会在当前账套内自动创建它，留着旧字典会让下一笔的候选里仍然没有它。清空后由下一次
   *    需要候选时补拉（与账户缓存同一时机，两者都是记账能改变的事实）。
   */
  function clear(): void {
    categories.value = []
  }

  return {
    categories,
    loading,
    list,
    create,
    update,
    setActive,
    clear,
  }
})
