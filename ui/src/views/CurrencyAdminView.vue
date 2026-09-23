<script setup lang="ts">
/**
 * /admin/currencies 页面：系统管理员的币种管理台。
 *
 * 币种是**全系统共用的字典**，不挂在账套下——本页的操作与当前账套无关，
 * 切换账套不会影响它，页面也不随账套变化而重载。
 *
 * 删除为**软删除**：以【停用】/【启用】取代删除，故按钮文案统一用「停用」而非「删除」。
 * 账户会绑定币种，物理删除会让既有账户指向一个不存在的币种、历史金额失去计价单位。
 *
 * 页面上「默认币种不能停用」「已停用的币种不能设为默认」等禁用态只是体验层的预先提示，
 * 后端对同一规则有独立校验，绕过前端只会拿到 4xx。
 */
import { computed, nextTick, onMounted, ref } from 'vue'
import { ApiError } from '@/api/http'
import { useCurrenciesStore, type Currency } from '@/stores/currencies'

const currenciesStore = useCurrenciesStore()

const errorMessage = ref('')
const notice = ref('')

/** 新建表单。 */
const newCode = ref('')
const newName = ref('')
const newSymbol = ref('')
const newSortOrder = ref<string | number>('0')

/** 自定义币种 Id 从 1 起自增，`0` 可安全用作「新建表单提交中」的哨兵值。 */
const NEW_ID = 0
/** 正在提交的币种 Id（`NEW_ID` 表示新建）；用于禁用按钮、避免重复提交。 */
const pendingId = ref<number | null>(null)
/** 正在修改的币种 Id 及名称/符号/排序草稿；`null` 表示当前没有行处于编辑态。 */
const editingId = ref<number | null>(null)
const draftName = ref('')
const draftSymbol = ref('')
const draftSortOrder = ref<string | number>('0')
/** 待二次确认停用的币种 Id。 */
const confirmingId = ref<number | null>(null)

/** 新建弹窗是否可见。 */
const createOpen = ref(false)
/** 新建弹窗的面板元素：打开时聚焦，使 `Esc` 监听在弹窗内生效。 */
const createPanelRef = ref<HTMLElement | null>(null)

const currencies = computed(() => currenciesStore.adminList)

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
    await currenciesStore.listAll()
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
    await currenciesStore.listAll()
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载币种列表失败'
  }
}

/**
 * 把输入框中的排序值解析为整数。
 *
 * 用 `Number` 而非 `parseInt`：后者会把 `"3abc"` 读成 `3`，让脏输入蒙混过关。
 * 空串按 `0` 处理——排序值只是一个呈现顺序，没有值时排在前面即可，不必为此拦住用户。
 */
function parseSortOrder(raw: string | number): number | null {
  const trimmed = String(raw).trim()
  if (trimmed.length === 0) {
    return 0
  }

  const parsed = Number(trimmed)
  return Number.isInteger(parsed) ? parsed : null
}

/** 打开新建弹窗：每次都把表单复位，避免上一次填了一半的内容留在弹窗里。 */
async function openCreate(): Promise<void> {
  newCode.value = ''
  newName.value = ''
  newSymbol.value = ''
  newSortOrder.value = '0'
  errorMessage.value = ''
  notice.value = ''
  createOpen.value = true

  await nextTick()
  createPanelRef.value?.focus()
}

/** 关闭新建弹窗（提交中不关：请求已发出，此刻关掉会让用户以为没建成功而重试）。 */
function closeCreate(): void {
  if (pendingId.value !== null) {
    return
  }

  createOpen.value = false
}

/** 新建币种；成功后关闭弹窗。 */
async function createCurrency(): Promise<void> {
  const code = newCode.value.trim()
  if (code.length === 0) {
    errorMessage.value = '请填写币种代码'
    return
  }

  const name = newName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '请填写币种名称'
    return
  }

  const sortOrder = parseSortOrder(newSortOrder.value)
  if (sortOrder === null) {
    errorMessage.value = '请填写有效的排序值（整数）'
    return
  }

  const symbol = newSymbol.value.trim()

  const ok = await run(async () => {
    await currenciesStore.create({
      code,
      name,
      symbol: symbol.length === 0 ? null : symbol,
      sortOrder,
    })
    notice.value = `已新建币种 ${code.toUpperCase()} ${name}`
  }, NEW_ID)

  if (ok) {
    createOpen.value = false
  }
}

/** 进入行内编辑：载入名称/符号/排序草稿（代码不可改，不参与编辑）。 */
function startEdit(currency: Currency): void {
  editingId.value = currency.id
  draftName.value = currency.name
  draftSymbol.value = currency.symbol ?? ''
  draftSortOrder.value = String(currency.sortOrder)
}

/** 保存行内编辑。 */
async function saveEdit(currency: Currency): Promise<void> {
  const name = draftName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '币种名称不能为空'
    return
  }

  const sortOrder = parseSortOrder(draftSortOrder.value)
  if (sortOrder === null) {
    errorMessage.value = '请填写有效的排序值（整数）'
    return
  }

  const symbol = draftSymbol.value.trim()

  const ok = await run(async () => {
    await currenciesStore.update(currency.id, {
      name,
      symbol: symbol.length === 0 ? null : symbol,
      sortOrder,
    })
    notice.value = `已保存币种 ${currency.code}`
  }, currency.id)

  if (ok) {
    editingId.value = null
  }
}

/** 设为系统默认币种。 */
async function setDefault(currency: Currency): Promise<void> {
  await run(async () => {
    await currenciesStore.setDefault(currency.id)
    notice.value = `已把 ${currency.code} ${currency.name} 设为默认币种`
  }, currency.id)
}

/** 启用或停用币种（停用需先点一次再确认）。 */
async function toggleActive(currency: Currency): Promise<void> {
  const target = !currency.isActive

  await run(async () => {
    await currenciesStore.setActive(currency.id, target)
    notice.value = target ? `已启用币种 ${currency.code}` : `已停用币种 ${currency.code}`
  }, currency.id)
}

onMounted(load)
</script>

<template>
  <main class="admin">
    <header class="head">
      <div>
        <h1 class="title">币种管理</h1>
        <p class="subtitle">
          币种是全系统共用的字典，与账套无关。账户创建时绑定币种且绑定后不可修改；
          只有币种相同的账户之间才能记账，故这里的币种应覆盖你实际会用到的全部货币。
          默认币种是新建账户与记账表单的初值，全系统至多一个。
        </p>
      </div>
      <div class="head-actions">
        <!-- 新增是本页的主操作：实心主色，与次要的「刷新」并列 -->
        <button type="button" class="submit" :disabled="pendingId !== null" @click="openCreate">
          新增
        </button>
        <button type="button" class="ghost" :disabled="currenciesStore.adminLoading" @click="load">
          {{ currenciesStore.adminLoading ? '刷新中…' : '刷新' }}
        </button>
      </div>
    </header>

    <!-- 弹窗打开时错误改在弹窗内呈现：页面级提示会被遮罩盖住，用户看不到 -->
    <p v-if="errorMessage && !createOpen" class="error">{{ errorMessage }}</p>
    <p v-if="notice" class="notice">{{ notice }}</p>

    <table class="table">
      <thead>
        <tr>
          <th>序号</th>
          <th>代码</th>
          <th>名称</th>
          <th>符号</th>
          <th class="sort">排序</th>
          <th>状态</th>
          <th class="actions-head">操作</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(currency, index) in currencies" :key="currency.id">
          <td class="row-index">{{ index + 1 }}</td>
          <!-- 代码不可修改：它是币种的身份，账户按代码绑定币种，故行内编辑也不给它控件 -->
          <td class="code">{{ currency.code }}</td>
          <td class="name">
            <input
              v-if="editingId === currency.id"
              v-model="draftName"
              type="text"
              maxlength="32"
              :disabled="pendingId !== null"
            />
            <template v-else>{{ currency.name }}</template>
          </td>
          <td class="symbol">
            <input
              v-if="editingId === currency.id"
              v-model="draftSymbol"
              type="text"
              maxlength="8"
              placeholder="可空"
              :disabled="pendingId !== null"
            />
            <template v-else>{{ currency.symbol ?? '—' }}</template>
          </td>
          <td class="sort">
            <input
              v-if="editingId === currency.id"
              v-model="draftSortOrder"
              type="number"
              step="1"
              :disabled="pendingId !== null"
            />
            <template v-else>{{ currency.sortOrder }}</template>
          </td>
          <td class="state">
            <span v-if="currency.isDefault" class="badge badge-default">默认</span>
            <span class="badge" :class="currency.isActive ? 'badge-active' : 'badge-inactive'">
              {{ currency.isActive ? '已启用' : '已停用' }}
            </span>
          </td>
          <td class="actions">
            <template v-if="editingId === currency.id">
              <button
                type="button"
                class="ghost"
                :disabled="pendingId !== null"
                @click="saveEdit(currency)"
              >
                保存
              </button>
              <button
                type="button"
                class="ghost"
                :disabled="pendingId !== null"
                @click="editingId = null"
              >
                取消
              </button>
            </template>
            <template v-else>
              <button
                type="button"
                class="ghost"
                :disabled="pendingId !== null"
                @click="startEdit(currency)"
              >
                编辑
              </button>
              <!-- 已是默认币种时不再给「设为默认」：点了等于什么都没发生 -->
              <button
                v-if="!currency.isDefault"
                type="button"
                class="ghost"
                :disabled="pendingId !== null || !currency.isActive"
                @click="setDefault(currency)"
              >
                设为默认
              </button>
            </template>

            <!-- 停用为软删除，需二次确认；启用无需确认 -->
            <template v-if="currency.isActive">
              <template v-if="confirmingId === currency.id">
                <button
                  type="button"
                  class="danger"
                  :disabled="pendingId !== null"
                  @click="toggleActive(currency)"
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
              <!-- 默认币种不可停用：它是新建账户与历史回填的兜底取值，后端同样会拒绝 -->
              <button
                v-else
                type="button"
                class="danger"
                :disabled="pendingId !== null || currency.isDefault"
                :title="currency.isDefault ? '默认币种不能停用，请先把另一个币种设为默认币种' : ''"
                @click="confirmingId = currency.id"
              >
                停用
              </button>
            </template>
            <button
              v-else
              type="button"
              class="ghost"
              :disabled="pendingId !== null"
              @click="toggleActive(currency)"
            >
              启用
            </button>
          </td>
        </tr>
        <tr v-if="!currenciesStore.adminLoading && currencies.length === 0">
          <td class="empty" colspan="7">暂无币种</td>
        </tr>
      </tbody>
    </table>

    <p class="hint">
      停用币种后，既有账户与流水照常可用（历史金额仍有计价单位），但新建账户与记账都不再能选它。
      默认币种不能停用：它是新建账户与历史账户回填的兜底取值，确需停用请先把另一个币种设为默认。
    </p>

    <!-- 新建弹窗：形态与账户页的弹窗一致（遮罩 + 面板 + 打开即聚焦，Esc / 点遮罩关闭） -->
    <div v-if="createOpen" class="mask" @click="closeCreate">
      <div
        ref="createPanelRef"
        class="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="currency-create-title"
        tabindex="-1"
        @click.stop
        @keydown.esc="closeCreate"
      >
        <h2 id="currency-create-title" class="dialog-title">新增币种</h2>
        <p class="dialog-subtitle">
          代码一经创建不可修改：账户按代码绑定币种，中途改代码等于让已绑定的账户指向另一个币种。
          需要更正代码时应停用后重新创建。
        </p>

        <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

        <div class="dialog-form">
          <label class="field">
            <span class="label">代码</span>
            <input
              v-model="newCode"
              type="text"
              maxlength="8"
              placeholder="必填，如 CNY（仅字母，全局唯一）"
              :disabled="pendingId !== null"
            />
          </label>
          <label class="field">
            <span class="label">名称</span>
            <input
              v-model="newName"
              type="text"
              maxlength="32"
              placeholder="必填，如 人民币"
              :disabled="pendingId !== null"
            />
          </label>
          <label class="field">
            <span class="label">符号</span>
            <input
              v-model="newSymbol"
              type="text"
              maxlength="8"
              placeholder="可选，如 ¥"
              :disabled="pendingId !== null"
            />
          </label>
          <label class="field">
            <span class="label">排序</span>
            <input v-model="newSortOrder" type="number" step="1" :disabled="pendingId !== null" />
          </label>
        </div>

        <p class="dialog-hint">
          新建的币种一律启用、且不是默认币种；默认币种由列表中的【设为默认】显式指定。
        </p>

        <div class="dialog-actions">
          <button
            type="button"
            class="submit"
            :disabled="pendingId !== null"
            @click="createCurrency"
          >
            {{ pendingId === NEW_ID ? '保存中…' : '保存' }}
          </button>
          <button type="button" class="ghost" :disabled="pendingId !== null" @click="closeCreate">
            取消
          </button>
        </div>
      </div>
    </div>
  </main>
</template>

<style scoped>
.admin {
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

/* 页头操作区：主操作「新增」在前，次要的「刷新」在后 */
.head-actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 0.5rem;
}

.error,
.notice,
.hint {
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

.hint {
  padding: 1rem 1.25rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-card);
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.8;
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

.table input {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  box-shadow: var(--shadow-control);
}

.table input:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

.table .name input,
.table .symbol input {
  width: 100%;
}

/* 列表行号：按当前列表顺序连续编号，与币种 Id 无关（停用币种后不会断号） */
.row-index {
  opacity: 0.6;
  white-space: nowrap;
}

/* 代码是这行的身份：加粗并等宽，便于纵向比对 */
.code {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  letter-spacing: 0.02em;
}

.name {
  font-weight: 600;
}

.symbol {
  white-space: nowrap;
}

.sort {
  width: 6rem;
  font-variant-numeric: tabular-nums;
}

.state {
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

.state .badge + .badge {
  margin-left: 0.35rem;
}

.badge-default {
  border-color: var(--color-accent);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
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

.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
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

/* 实心主色按钮：页头「新增」与弹窗「保存」共用的强视觉锚点（与登录页提交按钮同源） */
.submit {
  display: inline-flex;
  align-items: center;
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
