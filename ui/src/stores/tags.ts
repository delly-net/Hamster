/**
 * 标签状态。
 *
 * 标签归属且仅归属一个账套，列表按**当前账套**过滤：请求由 `api/http.ts` 自动附带
 * `X-Account-Set-Id`，此处不做账套判断——切换账套后的重新拉取由页面 watch 当前账套负责。
 *
 * 与分类**逐条同构**：标签没有可见性维度（账套内所有成员看到的是同一份完整词汇表），
 * 也没有「他人私有的标签」这一概念；维护它不需要管理员身份（这与币种刻意相反：
 * 币种是全局字典，改动影响所有账套，故维护能力收在管理端）。
 * 与分类**唯一的差别是基数**：一笔交易至多一个分类（分类是交易头上的一列），
 * 而一笔交易可以有多个标签（落在 `hamster_transaction_tag` 子表里）。
 *
 * 标签**不区分记账类型**：同一份词汇表供收入/支出/转账三类共用。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/**
 * 标签（后端不回传内部明细）。
 *
 * 描述的是**字典里的一条标签**。「某笔交易挂着哪些标签」是另一件事，见 {@link TagRef}。
 */
export interface Tag {
  id: number
  accountSetId: number
  /** 标签名称；交易写入时上报的 `tagIds` 即 {@link id}。 */
  name: string
  /** 是否启用；`false` 表示已停用（软删除）。 */
  isActive: boolean
  /** 创建时间（UTC，ISO 8601）。 */
  createdAt: string
}

/**
 * **某笔交易上的一个标签**（交易与明细接口回传的形状）。
 *
 * 与 {@link Tag} 分开是刻意的：那个描述的是字典里的一条记录（带账套、启用状态、创建时间），
 * 此处描述的是「这笔账当时被标了什么」——界面需要的只有「显示什么名字」与「回传什么主键」，
 * 账套与创建时间在这里是噪音。
 *
 * **没有 `isActive`**：交易挂着的标签可能是已停用的（停用是「不再供新记账选择」，
 * 不是「历史上从未用过」）。界面一律按现有名字原样呈现，不给已停用的标签加灰或划线——
 * 用户看的是「这笔账当时标了什么」，不是「这份词汇表现在长什么样」。
 * 停用状态只在标签管理页里呈现。
 */
export interface TagRef {
  id: number
  /** 标签名称（可能是已停用标签的名称）。 */
  name: string
}

/**
 * **界面上「还没落库」的标签在 {@link TagRef} 里的主键占位值。**
 *
 * 标签主键由后端从 1 起分配，`0` 永不可能是真实主键，故可安全用作「这个名字在账套里还不存在，
 * 提交时按名字交给后端自动创建」的标记——与 `TagView.vue` 里新建表单的 `NEW_ID = 0` 同一约定。
 *
 * 用哨兵而不是另立一个 `{ name }` 联合类型：选择框内部因此只有**一个**已选中标签的数组，
 * 「删掉一个标签」只需删一次；而「哪些已落库、哪些待创建」是提交那一刻的切分，
 * 由 {@link splitTagRefs} 现算——两处各存一份迟早会出现「删了芯片、名字还留在载荷里」。
 *
 * **注意它只是界面草稿的约定，不是接口约定**：接口收的是两份独立载荷（`tagIds` + `tagNames`），
 * 里面从来没有 `0` 这个值。
 */
export const NEW_TAG_ID = 0

/**
 * 把界面上的标签草稿切成接口要的两份载荷。
 *
 * 后端对这两份是**合并**语义（各自都可空、可同时有值），故此处不必在两者间做取舍，
 * 只需按「是否已有主键」分流：已落库的走 `tagIds`，手工输入的新名字走 `tagNames`
 * （名字命中的可能是**已停用**的标签，后端会归到它上面而不是另建一个同名的）。
 *
 * 顺序原样保留：后端按提交次序写关联行，界面回显时也按这个次序，两处一致。
 *
 * 记账与改账共用本函数（两个调用点），写在 store 里而不是各自就地算——
 * 两处若各写一份分流，迟早漂移成「记账传名字、改账传主键」这种对不上的契约。
 */
export function splitTagRefs(refs: TagRef[]): { tagIds: number[]; tagNames: string[] } {
  const tagIds: number[] = []
  const tagNames: string[] = []

  for (const ref of refs) {
    if (ref.id === NEW_TAG_ID) {
      tagNames.push(ref.name)
    } else {
      tagIds.push(ref.id)
    }
  }

  return { tagIds, tagNames }
}

/** 新建标签的入参。 */
export interface CreateTagPayload {
  name: string
}

/**
 * 修改标签的入参。
 *
 * **只有名称**：所属账套一经创建不可修改——改归属等于把这个标签从一家的词汇表搬到另一家，
 * 而挂着它的历史流水并不跟着搬家。后端 `PUT` 的请求体同样只有名称。
 */
export interface UpdateTagPayload {
  name: string
}

/** 接口基址。 */
const TAGS_PATH = '/api/tags'

export const useTagsStore = defineStore('tags', () => {
  /** 当前账套内的标签。 */
  const tags = ref<Tag[]>([])
  const loading = ref(false)

  /**
   * 拉取当前账套内的标签。
   *
   * @param includeInactive 是否包含已停用的标签；记账表单用默认的 `false`（停用的不该再被选），
   * 标签管理页与账目明细页的筛选区用 `true`（前者要能看见并重新启用它们，
   * 后者要能用一个已停用的标签去筛出历史账——**停用挡的是「新记账选它」，
   * 不是「不能再拿它查历史」**）。
   * @throws 未选择账套时后端返回 400；令牌失效或网络异常时抛出 `ApiError`。
   */
  async function list(includeInactive = false): Promise<Tag[]> {
    loading.value = true
    try {
      const result = await request<Tag[]>(`${TAGS_PATH}?includeInactive=${includeInactive}`)
      tags.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /**
   * 新建标签；名称重复时后端返回 409。
   *
   * 记账表单**不需要**先调本方法：后端在收到候选之外的标签名时会自动创建它。
   * 本方法是标签管理页「事先建好一份词汇表」的入口。
   */
  async function create(payload: CreateTagPayload): Promise<void> {
    await request<Tag>(TAGS_PATH, { method: 'POST', body: payload })
  }

  /** 修改标签名称；改名后历史明细自动显示新名字（关联行挂的是主键，不是名称）。 */
  async function update(id: number, payload: UpdateTagPayload): Promise<void> {
    await request<void>(`${TAGS_PATH}/${id}`, { method: 'PUT', body: payload })
  }

  /** 启用或停用标签（停用即软删除，数据行保留）。 */
  async function setActive(id: number, isActive: boolean): Promise<void> {
    await request<void>(`${TAGS_PATH}/${id}/${isActive ? 'activate' : 'deactivate'}`, {
      method: 'POST',
    })
  }

  /**
   * 清空列表。
   *
   * 两个调用方、同一件事——**手上的这份标签词汇表已不再是事实**：
   * 1. 退出登录或账套失效（避免残留上一账套的标签）；
   * 2. **记账提交成功后**（见 `EntryRecordForm.vue`）：记账人打出一个候选里没有的标签名时，
   *    后端会在当前账套内自动创建它，留着旧词汇表会让下一笔的候选里仍然没有它。清空后由下一次
   *    需要候选时补拉（与账户、分类同一时机——三者都是记账能改变的事实）。
   */
  function clear(): void {
    tags.value = []
  }

  return {
    tags,
    loading,
    list,
    create,
    update,
    setActive,
    clear,
  }
})
