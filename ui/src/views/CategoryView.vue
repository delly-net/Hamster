<script setup lang="ts">
/**
 * /categories 页面：当前账套内的分类管理。
 *
 * 分类一律挂在账套下，故页面严格跟随「当前账套」：未选择账套时只提示、不渲染表格，
 * 账套切换时重新拉取（否则会残留上一账套的分类）。
 *
 * **本页不需要管理员身份**：分类是账套内所有成员共用的字典，与账户同档，而非币种那样的全局字典。
 * 也不做任何本地过滤——后端按当前账套返回全部条目，可见性这一维度对分类根本不存在
 * （没有「他人私有的分类」）。
 *
 * 与账户页的两处刻意的不同：
 * - **没有类型页签**：分类不区分收入/支出/转账，同一份字典三类共用，故没有可分的组。
 * - **没有币种**：分类与金额的计价单位无关。故本页比账户页少拉一次币种字典。
 *
 * 新建与修改统一走弹窗（【新增】/【编辑】→ 表单 →【保存】落表），与账户页同构。
 * 修改的可编辑字段只有名称：所属账套是分类的归属，一经创建不可修改，故在弹窗内只读呈现。
 *
 * 停用即软删除（数据行保留、可重新启用），故按钮文案统一用「停用」而非「删除」。
 * 停用后该分类不再出现在记账表单的候选中，但**已挂它的历史明细照常显示其名称**。
 */
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import { useAccountSetsStore } from '@/stores/accountSets'
import { useCategoriesStore, type Category } from '@/stores/categories'

const accountSets = useAccountSetsStore()
const categoriesStore = useCategoriesStore()

/** 后端回传的是带 `Z` 的 UTC 时间，交给 `Intl` 按浏览器本地时区呈现。 */
const dateTimeFormatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'short',
})

/** 分类名最大长度，与后端 `Category.Name` 的列长一致。 */
const NAME_MAX_LENGTH = 32

const errorMessage = ref('')
const notice = ref('')

/** 是否呈现已停用的分类（软删除后的分类只在此模式下可见，并可被重新启用）。 */
const showInactive = ref(false)

/** 新建弹窗是否可见。 */
const createOpen = ref(false)

/** 新建弹窗的面板元素：打开时聚焦，使 `Esc` 监听在弹窗内生效。 */
const createPanelRef = ref<HTMLElement | null>(null)

/** 新建表单的名称草稿。 */
const newName = ref('')

/** 自定义分类 Id 从 1 起自增，`0` 可安全用作「新建表单提交中」的哨兵值。 */
const NEW_ID = 0
/** 正在提交的分类 Id（`NEW_ID` 表示新建）；用于禁用按钮、避免重复提交。 */
const pendingId = ref<number | null>(null)

/**
 * 正在修改的分类；`null` 表示修改弹窗已关闭。
 *
 * 弹窗内的只读上下文（所属账套、创建时间）直接取自这个对象，是**打开时的快照**：
 * 唯一能改动分类的操作是保存成功，而成功即关窗，故快照不会有机会变成陈旧显示。
 */
const editTarget = ref<Category | null>(null)

/** 修改弹窗中可编辑的名称草稿；所属账套与创建时间均不可修改。 */
const editName = ref('')

/** 修改弹窗的面板元素：打开时聚焦，使 `Esc` 监听在弹窗内生效。 */
const editPanelRef = ref<HTMLElement | null>(null)

/** 待二次确认停用的分类 Id。 */
const confirmingId = ref<number | null>(null)

const categories = computed(() => categoriesStore.categories)

/** 是否已选定账套；未选定时页面只提示，不展示分类数据。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/**
 * 当前账套名称，用于修改弹窗的只读上下文。
 *
 * 取自已加载的账套列表（`current`），**不回退到账套主键**：主键对用户没有意义，
 * 而账套列表在进入本页前必然已加载过（未选定账套时本页连表格都不渲染）。
 */
const accountSetName = computed(() => accountSets.current?.name ?? '')

/** 格式化 ISO 时间；无法解析时原样回显。 */
function formatDateTime(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : dateTimeFormatter.format(parsed)
}

/** 统一包装操作：维护 pending 态、刷新列表、呈现失败原因。 */
async function run(action: () => Promise<void>, pending: number): Promise<boolean> {
  if (pendingId.value !== null) {
    return false
  }

  pendingId.value = pending
  errorMessage.value = ''
  notice.value = ''

  try {
    await action()
    await categoriesStore.list(showInactive.value)
    return true
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '操作失败，请稍后重试'
    return false
  } finally {
    pendingId.value = null
    confirmingId.value = null
  }
}

async function load(): Promise<void> {
  errorMessage.value = ''
  try {
    await categoriesStore.list(showInactive.value)
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载分类失败'
  }
}

/** 打开新建弹窗：每次都把表单复位，避免上一次填了一半的内容留在弹窗里。 */
async function openCreate(): Promise<void> {
  newName.value = ''
  errorMessage.value = ''
  notice.value = ''
  createOpen.value = true

  await nextTick()
  createPanelRef.value?.focus()
}

/**
 * 关闭新建弹窗。
 *
 * 提交中不允许关闭：请求已经发出，此刻关掉弹窗会让用户以为「没建成功」，
 * 而去重试一个其实已经落表了的分类。
 */
function closeCreate(): void {
  if (pendingId.value !== null) {
    return
  }

  createOpen.value = false
}

/** 新建分类；成功后关闭弹窗。 */
async function createCategory(): Promise<void> {
  const name = newName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '请填写分类名称'
    return
  }

  if (name.length > NAME_MAX_LENGTH) {
    errorMessage.value = `分类名称不能超过 ${NAME_MAX_LENGTH} 位`
    return
  }

  const ok = await run(async () => {
    await categoriesStore.create({ name })
    notice.value = `已新建分类 ${name}`
  }, NEW_ID)

  if (ok) {
    createOpen.value = false
  }
}

/** 打开修改弹窗：载入名称草稿并复位上一次的提示。 */
async function openEdit(category: Category): Promise<void> {
  editTarget.value = category
  editName.value = category.name
  errorMessage.value = ''
  notice.value = ''

  await nextTick()
  editPanelRef.value?.focus()
}

/** 关闭修改弹窗（与新建弹窗同规则：提交中不关，理由见 `closeCreate`）。 */
function closeEdit(): void {
  if (pendingId.value !== null) {
    return
  }

  editTarget.value = null
}

/** 保存修改。所属账套不可修改，故请求体只有名称。 */
async function saveEdit(): Promise<void> {
  const target = editTarget.value
  if (target === null) {
    return
  }

  const name = editName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '分类名称不能为空'
    return
  }

  if (name.length > NAME_MAX_LENGTH) {
    errorMessage.value = `分类名称不能超过 ${NAME_MAX_LENGTH} 位`
    return
  }

  const ok = await run(async () => {
    await categoriesStore.update(target.id, { name })
    notice.value = `已保存分类 ${name}`
  }, target.id)

  if (ok) {
    editTarget.value = null
  }
}

/** 停用或启用分类（停用需先点一次再确认）。 */
async function toggleActive(category: Category): Promise<void> {
  const target = !category.isActive

  await run(async () => {
    await categoriesStore.setActive(category.id, target)
    notice.value = target ? `已启用分类 ${category.name}` : `已停用分类 ${category.name}`
  }, category.id)
}

// 账套切换后必须重新拉取：分类是按账套隔离的，沿用旧列表会显示上一账套的数据。
// 未选择账套时清空列表，避免退出登录后仍残留可见数据。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    confirmingId.value = null
    // 弹窗里填的是上一个账套的分类，换账套后不该继续沿用（两个弹窗都要关）
    createOpen.value = false
    editTarget.value = null
    notice.value = ''

    if (currentId === null) {
      categoriesStore.clear()
      return
    }

    await load()
  },
)

// 切换「显示已停用」即时生效，无需用户再点一次刷新
watch(showInactive, () => {
  if (hasAccountSet.value) {
    void load()
  }
})

onMounted(() => {
  if (hasAccountSet.value) {
    void load()
  }
})
</script>

<template>
  <main class="categories">
    <header class="head">
      <div>
        <h1 class="title">分类管理</h1>
        <p class="subtitle">
          分类归属当前账套，账套内成员共用一份字典，<strong>不区分收入/支出/转账</strong>。
          记账时可直接输入一个新名字，后端会顺手把它建成分类，故本页并非必需的准备工作，
          而是用来把常用分类事先备好、以及给既有分类改名或停用的地方。
        </p>
      </div>
      <div class="head-actions">
        <!-- 新增是本页的主操作：实心主色，与次要的「刷新」并列 -->
        <button
          type="button"
          class="submit"
          :disabled="pendingId !== null || !hasAccountSet"
          @click="openCreate"
        >
          新增
        </button>
        <button
          type="button"
          class="ghost"
          :disabled="categoriesStore.loading || !hasAccountSet"
          @click="load"
        >
          {{ categoriesStore.loading ? '刷新中…' : '刷新' }}
        </button>
      </div>
    </header>

    <!-- 未选定账套：分类必须落在某个账套内，此时不渲染弹窗与表格 -->
    <p v-if="!hasAccountSet" class="hint">
      当前未选择账套，请先点击右上角的【切换】选择账套后再管理分类。
    </p>

    <template v-else>
      <!-- 弹窗打开时错误改在弹窗内呈现：页面级提示会被遮罩盖住，用户看不到 -->
      <p v-if="errorMessage && !createOpen && editTarget === null" class="error">
        {{ errorMessage }}
      </p>
      <p v-if="notice" class="notice">{{ notice }}</p>

      <div class="toolbar">
        <span class="toolbar-count">共 {{ categories.length }} 个分类</span>

        <label class="toggle">
          <input v-model="showInactive" type="checkbox" :disabled="categoriesStore.loading" />
          <span>显示已停用分类</span>
        </label>
      </div>

      <table class="table">
        <thead>
          <tr>
            <th>序号</th>
            <th>分类名称</th>
            <th>状态</th>
            <th>创建时间</th>
            <th class="actions-head">操作</th>
          </tr>
        </thead>
        <tbody>
          <!-- 序号按当前列表重排：停用后不显示时行号随之连续，不留断号 -->
          <tr v-for="(category, index) in categories" :key="category.id">
            <td class="row-index">{{ index + 1 }}</td>
            <td class="name">{{ category.name }}</td>
            <td>
              <span class="badge" :class="category.isActive ? 'badge-active' : 'badge-inactive'">
                {{ category.isActive ? '已启用' : '已停用' }}
              </span>
            </td>
            <td class="created">{{ formatDateTime(category.createdAt) }}</td>
            <td class="actions">
              <button
                type="button"
                class="ghost"
                :disabled="pendingId !== null"
                @click="openEdit(category)"
              >
                编辑
              </button>

              <!-- 停用为软删除，需二次确认；启用无需确认 -->
              <template v-if="category.isActive">
                <template v-if="confirmingId === category.id">
                  <button
                    type="button"
                    class="danger"
                    :disabled="pendingId !== null"
                    @click="toggleActive(category)"
                  >
                    确认停用
                  </button>
                  <button
                    type="button"
                    class="ghost"
                    :disabled="pendingId !== null"
                    @click="confirmingId = null"
                  >
                    取消
                  </button>
                </template>
                <button
                  v-else
                  type="button"
                  class="danger"
                  :disabled="pendingId !== null"
                  @click="confirmingId = category.id"
                >
                  停用
                </button>
              </template>
              <button
                v-else
                type="button"
                class="ghost"
                :disabled="pendingId !== null"
                @click="toggleActive(category)"
              >
                启用
              </button>
            </td>
          </tr>
          <tr v-if="!categoriesStore.loading && categories.length === 0">
            <td class="empty" colspan="5">
              {{ showInactive ? '当前账套内还没有分类' : '当前账套内还没有启用的分类' }}
            </td>
          </tr>
        </tbody>
      </table>

      <p class="hint hint-foot">
        分类<strong>不区分收入/支出/转账</strong>：同一份字典三类记账共用，「餐饮」在哪一类下含义都一样，
        强制二选一只会让人为同一件事建两个分类。
        分类名在<strong>账套内不区分大小写唯一</strong>，重名会被拒绝。
        停用是软删除：数据行保留，可随时重新启用；<strong>已挂该分类的历史明细照常显示它的名称</strong>，
        且手工输入这个名字时仍会归到它上面（不会另建一个同名的，那样历史明细会裂成两份）。
      </p>
    </template>

    <!-- 新建弹窗：形态与账户页一致（遮罩 + 面板 + 打开即聚焦，Esc / 点遮罩关闭） -->
    <div v-if="createOpen" class="mask" @click="closeCreate">
      <div
        ref="createPanelRef"
        class="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="category-create-title"
        tabindex="-1"
        @click.stop
        @keydown.esc="closeCreate"
      >
        <h2 id="category-create-title" class="dialog-title">新增分类</h2>
        <p class="dialog-subtitle">
          分类归属当前账套{{ accountSetName ? `（${accountSetName}）` : '' }}，账套内成员共用，
          创建后不可转移到其它账套。
        </p>

        <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

        <div class="dialog-form">
          <label class="field">
            <span class="label">名称</span>
            <input
              v-model="newName"
              type="text"
              :maxlength="NAME_MAX_LENGTH"
              placeholder="必填，同一账套内不重名"
              :disabled="pendingId !== null"
            />
          </label>
        </div>

        <p class="dialog-hint">
          新建的分类一律启用，且<strong>不区分收入/支出/转账</strong>。记账表单里直接输入一个新名字
          也会建出分类来，故这里只需备好常用的那几个。
        </p>

        <div class="dialog-actions">
          <button
            type="button"
            class="submit"
            :disabled="pendingId !== null"
            @click="createCategory"
          >
            {{ pendingId === NEW_ID ? '保存中…' : '保存' }}
          </button>
          <button type="button" class="ghost" :disabled="pendingId !== null" @click="closeCreate">
            取消
          </button>
        </div>
      </div>
    </div>

    <!-- 修改弹窗：与新建弹窗同构（遮罩 + 面板 + 打开即聚焦，Esc / 点遮罩关闭） -->
    <div v-if="editTarget" class="mask" @click="closeEdit">
      <div
        ref="editPanelRef"
        class="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="category-edit-title"
        tabindex="-1"
        @click.stop
        @keydown.esc="closeEdit"
      >
        <h2 id="category-edit-title" class="dialog-title">修改分类</h2>
        <p class="dialog-subtitle">只有名称可以修改：所属账套一经创建即不可变更。</p>

        <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

        <div class="dialog-form">
          <label class="field">
            <span class="label">名称</span>
            <input
              v-model="editName"
              type="text"
              :maxlength="NAME_MAX_LENGTH"
              placeholder="必填，同一账套内不重名"
              :disabled="pendingId !== null"
            />
          </label>
        </div>

        <!-- 不可修改的字段一律只读文本呈现：不给控件，连禁用的也不给 -->
        <dl class="dialog-readonly">
          <div class="readonly-row">
            <dt>所属账套</dt>
            <dd>{{ accountSetName || '当前账套' }}</dd>
          </div>
          <div class="readonly-row">
            <dt>创建时间</dt>
            <dd>{{ formatDateTime(editTarget.createdAt) }}</dd>
          </div>
        </dl>

        <p class="dialog-hint">
          改名<strong>不影响既有明细</strong>：流水挂的是分类主键而不是名称，改完之后历史明细会直接显示新名字。
        </p>

        <div class="dialog-actions">
          <button type="button" class="submit" :disabled="pendingId !== null" @click="saveEdit">
            {{ pendingId === editTarget.id ? '保存中…' : '保存' }}
          </button>
          <button type="button" class="ghost" :disabled="pendingId !== null" @click="closeEdit">
            取消
          </button>
        </div>
      </div>
    </div>
  </main>
</template>

<style scoped>
.categories {
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

/* 页脚说明是补充信息，与顶部的空态提示区分开：不占满整行视觉重心 */
.hint-foot {
  font-size: 12.5px;
  opacity: 0.7;
}

.error,
.notice {
  padding: 0.5rem 0.7rem;
  border-radius: var(--radius-control);
  font-size: 13px;
  line-height: 1.7;
}

.error {
  border: 1px solid var(--color-danger-border);
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

/* 提示态复用主色：全站只有 danger 一种语义色，不额外引入绿色 */
.notice {
  border: 1px solid var(--color-accent);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
}

/* 页头操作区：主操作「新增」在前，次要的「刷新」在后 */
.head-actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 0.5rem;
}

/* 计数靠左、开关靠右：与账户页的页签条+开关同行同构 */
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
}

.toolbar-count {
  font-size: 12.5px;
  opacity: 0.7;
}

.toggle {
  display: flex;
  flex: none;
  align-items: center;
  gap: 0.35rem;
  font-size: 13px;
  opacity: 0.85;
  cursor: pointer;
}

.table {
  width: 100%;
  border-collapse: collapse;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  overflow: hidden;
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
  font-size: 13.5px;
}

.table th,
.table td {
  padding: 0.65rem 0.75rem;
  text-align: left;
  border-bottom: 1px solid var(--color-border);
  vertical-align: middle;
}

.table thead th {
  font-size: 12.5px;
  font-weight: 600;
  opacity: 0.75;
  background: var(--color-background-mute);
}

.table tbody tr:last-child td {
  border-bottom: 0;
}

/* 列表行号：按当前列表顺序连续编号，与分类 Id 无关（停用后不显示时不会断号） */
.row-index {
  opacity: 0.6;
  white-space: nowrap;
}

.name {
  font-weight: 600;
}

.created {
  opacity: 0.75;
  white-space: nowrap;
}

.badge {
  display: inline-block;
  padding: 0.15rem 0.5rem;
  border-radius: 999px;
  border: 1px solid transparent;
  font-size: 12px;
  white-space: nowrap;
}

.badge-active {
  border-color: var(--color-border-hover);
  opacity: 0.8;
}

.badge-inactive {
  border-color: var(--color-danger-border);
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
}

.actions-head {
  white-space: nowrap;
}

.empty {
  text-align: center;
  padding: 1.5rem;
  opacity: 0.6;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  min-width: 9rem;
}

.label {
  font-size: 12.5px;
  opacity: 0.75;
}

.field input {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  box-shadow: var(--shadow-control);
}

.field input:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

/* 遮罩层级高于窄屏抽屉与遮罩（z-index 19/20）：弹窗必须盖住侧栏 */
.mask {
  position: fixed;
  inset: 0;
  z-index: 30;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 1rem;
  background-color: rgba(0, 0, 0, 0.32);
}

.dialog {
  width: 100%;
  max-width: 30rem;
  max-height: 85vh;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1.5rem 1.5rem 1.25rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
  /* 窄屏下弹窗可能高于视口：自身滚动，不撑破遮罩 */
  overflow-y: auto;
}

.dialog-title {
  font-size: 18px;
  font-weight: 600;
  color: var(--color-heading);
}

.dialog-subtitle,
.dialog-hint {
  font-size: 12.5px;
  line-height: 1.7;
  opacity: 0.75;
}

.dialog-form {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}

/* 修改弹窗里的只读上下文：一列标签一列值，只求一眼看全 */
.dialog-readonly {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  padding: 0.6rem 0.7rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  background: var(--color-background-mute);
  font-size: 13px;
}

.readonly-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
}

.readonly-row dt {
  font-size: 12.5px;
  opacity: 0.75;
}

.readonly-row dd {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 0.25rem;
}

.ghost,
.danger,
.submit {
  padding: 0.3rem 0.7rem;
  border-radius: var(--radius-control);
  background: none;
  font-size: 12.5px;
  font-family: inherit;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s;
}

.ghost {
  border: 1px solid var(--color-accent);
  color: var(--color-accent-strong);
}

.danger {
  border: 1px solid var(--color-danger-border);
  color: var(--color-danger);
}

/* 实心主色按钮：页头「新增」与弹窗「保存」共用的强视觉锚点（与账户页同源） */
.submit {
  align-items: center;
  display: inline-flex;
  padding: 0.5rem 1rem;
  border: 1px solid var(--color-accent);
  background: var(--color-accent);
  color: var(--color-accent-contrast);
  font-size: 13px;
  font-weight: 600;
}

@media (hover: hover) {
  .ghost:not(:disabled):hover {
    background-color: var(--color-accent-soft);
  }

  .danger:not(:disabled):hover {
    background-color: var(--color-danger-soft);
  }

  .submit:not(:disabled):hover {
    border-color: var(--color-accent-strong);
    background: var(--color-accent-strong);
  }
}

.ghost:disabled,
.danger:disabled,
.submit:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
</style>
