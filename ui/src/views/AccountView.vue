<script setup lang="ts">
/**
 * /accounts 页面：当前账套内的账户管理。
 *
 * 账户一律挂在账套下，故页面严格跟随「当前账套」：未选择账套时只提示、不渲染表单与表格，
 * 账套切换时重新拉取（否则会残留上一账套的账户）。
 *
 * 页面上「个人账户只显示自己的」「公共账户人人可见」等现象都是后端判定的结果，
 * 本页不做任何本地过滤——绕过前端只会拿到 403 / 404。
 *
 * 账本账户（系统在期初入账时自动创建）同样由后端过滤：接口不返回它、也不接受把类型改成它，
 * 故本页既看不到它、也无法造出它，前端无需为此写任何过滤或禁用逻辑。
 *
 * 停用即软删除（数据行保留、可重新启用），故按钮文案统一用「停用」而非「删除」。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import { useAccountSetsStore } from '@/stores/accountSets'
import {
  ACCOUNT_SCOPE_LABELS,
  ACCOUNT_SCOPE_OPTIONS,
  ACCOUNT_TYPE_LABELS,
  ACCOUNT_TYPE_OPTIONS,
  useAccountsStore,
  type Account,
  type AccountScope,
  type AccountType,
} from '@/stores/accounts'

const accountSets = useAccountSetsStore()
const accountsStore = useAccountsStore()

/** 金额呈现：固定两位小数，与后端的 decimal(...,2) 对齐。 */
const amountFormatter = new Intl.NumberFormat('zh-CN', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/** 后端回传的是带 `Z` 的 UTC 时间，交给 `Intl` 按浏览器本地时区呈现。 */
const dateTimeFormatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'short',
})

const errorMessage = ref('')
const notice = ref('')

/** 是否呈现已停用的账户（软删除后的账户只在此模式下可见，并可被重新启用）。 */
const showInactive = ref(false)

/** 新建表单。 */
const newName = ref('')
const newScope = ref<AccountScope>('Personal')
const newType = ref<AccountType>('Fund')
/** 期初金额：输入框是 `type="number"`，v-model 会自动把值转成 number（空串除外），故此处必须是联合类型。 */
const newInitialBalance = ref<string | number>('0')

/** 自定义账户 Id 从 1 起自增，`0` 可安全用作「新建表单提交中」的哨兵值。 */
const NEW_ID = 0
/** 正在提交的账户 Id（`NEW_ID` 表示新建）；用于禁用按钮、避免重复提交。 */
const pendingId = ref<number | null>(null)

/** 正在行内编辑的账户 Id 及其草稿值；期初金额不可修改，故草稿中没有该字段。 */
const editingId = ref<number | null>(null)
const draftName = ref('')
const draftType = ref<AccountType>('Fund')

/** 待二次确认停用的账户 Id。 */
const confirmingId = ref<number | null>(null)

const accounts = computed(() => accountsStore.accounts)

/** 是否已选定账套；未选定时页面只提示，不展示账户数据。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/** 格式化金额；无法解析时原样回显。 */
function formatAmount(value: number): string {
  return Number.isFinite(value) ? amountFormatter.format(value) : String(value)
}

/** 格式化 ISO 时间；无法解析时原样回显。 */
function formatDateTime(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : dateTimeFormatter.format(parsed)
}

/**
 * 把输入框中的金额文本解析为数字。
 * 用 `Number` 而非 `parseFloat`：后者会把 `"12abc"` 读成 `12`，让脏输入蒙混过关。
 *
 * 入参可能是 number（`type="number"` 的 v-model 自动转型所致），故先 `String` 归一再解析。
 */
function parseAmount(raw: string | number): number | null {
  const trimmed = String(raw).trim()
  if (trimmed.length === 0) {
    return null
  }

  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : null
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
    await accountsStore.list(showInactive.value)
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
    await accountsStore.list(showInactive.value)
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载账户失败'
  }
}

/** 新建账户。 */
async function createAccount(): Promise<void> {
  const name = newName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '请填写账户名称'
    return
  }

  const initialBalance = parseAmount(newInitialBalance.value)
  if (initialBalance === null) {
    errorMessage.value = '请填写有效的期初金额'
    return
  }

  await run(async () => {
    await accountsStore.create({
      name,
      scope: newScope.value,
      type: newType.value,
      initialBalance,
    })
    newName.value = ''
    newInitialBalance.value = '0'
    notice.value = `已新建${ACCOUNT_SCOPE_LABELS[newScope.value]}账户 ${name}`
  }, NEW_ID)
}

/** 进入行内编辑状态。 */
function startEdit(account: Account): void {
  editingId.value = account.id
  draftName.value = account.name
  draftType.value = account.type
}

/** 保存行内编辑（归属范围与期初金额均不可修改，故草稿中没有这两个字段）。 */
async function saveEdit(account: Account): Promise<void> {
  const name = draftName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '账户名称不能为空'
    return
  }

  const ok = await run(async () => {
    await accountsStore.update(account.id, {
      name,
      type: draftType.value,
    })
    notice.value = `已保存账户 ${name}`
  }, account.id)

  if (ok) {
    editingId.value = null
  }
}

/** 停用或启用账户（停用需先点一次再确认）。 */
async function toggleActive(account: Account): Promise<void> {
  const target = !account.isActive

  await run(async () => {
    await accountsStore.setActive(account.id, target)
    notice.value = target ? `已启用账户 ${account.name}` : `已停用账户 ${account.name}`

    if (editingId.value === account.id) {
      editingId.value = null
    }
  }, account.id)
}

// 账套切换后必须重新拉取：账户是按账套隔离的，沿用旧列表会显示上一账套的数据。
// 未选择账套时清空列表，避免退出登录后仍残留可见数据。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    editingId.value = null
    confirmingId.value = null
    notice.value = ''

    if (currentId === null) {
      accountsStore.clear()
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
  <main class="accounts">
    <header class="head">
      <div>
        <h1 class="title">账户管理</h1>
        <p class="subtitle">
          账户归属当前账套：公共账户账套内成员共用，个人账户仅创建者本人可见可用。
          余额为派生值（该账户全部交易明细的有符号汇总，期初已计入其中），不可直接编辑。
        </p>
      </div>
      <button
        type="button"
        class="ghost"
        :disabled="accountsStore.loading || !hasAccountSet"
        @click="load"
      >
        {{ accountsStore.loading ? '刷新中…' : '刷新' }}
      </button>
    </header>

    <!-- 未选定账套：账户必须落在某个账套内，此时不渲染表单与表格 -->
    <p v-if="!hasAccountSet" class="hint">
      当前未选择账套，请先点击右上角的【切换】选择账套后再管理账户。
    </p>

    <template v-else>
      <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
      <p v-if="notice" class="notice">{{ notice }}</p>

      <section class="create-panel">
        <h2 class="panel-title">新建账户</h2>
        <div class="create-row">
          <label class="field">
            <span class="label">名称</span>
            <input
              v-model="newName"
              type="text"
              maxlength="64"
              placeholder="必填，同一归属范围内不重名"
              :disabled="pendingId !== null"
            />
          </label>
          <label class="field">
            <span class="label">归属范围</span>
            <select v-model="newScope" :disabled="pendingId !== null">
              <option v-for="option in ACCOUNT_SCOPE_OPTIONS" :key="option.value" :value="option.value">
                {{ option.label }}
              </option>
            </select>
          </label>
          <label class="field">
            <span class="label">账户类型</span>
            <select v-model="newType" :disabled="pendingId !== null">
              <option v-for="option in ACCOUNT_TYPE_OPTIONS" :key="option.value" :value="option.value">
                {{ option.label }}
              </option>
            </select>
          </label>
          <label class="field">
            <span class="label">期初金额</span>
            <input
              v-model="newInitialBalance"
              type="number"
              step="0.01"
              :disabled="pendingId !== null"
            />
          </label>
          <button
            type="button"
            class="submit"
            :disabled="pendingId !== null"
            @click="createAccount"
          >
            {{ pendingId === NEW_ID ? '创建中…' : '创建' }}
          </button>
        </div>
        <p class="panel-hint">
          个人账户的归属人固定为当前登录者，创建后不可转让；归属范围与所属账套一经创建不可修改。
          账本账户由系统在期初入账时自动创建，不接受手工建立，也不在本列表呈现。
        </p>
      </section>

      <div class="toolbar">
        <label class="toggle">
          <input v-model="showInactive" type="checkbox" :disabled="accountsStore.loading" />
          <span>显示已停用账户</span>
        </label>
      </div>

      <table class="table">
        <thead>
          <tr>
            <th>序号</th>
            <th>账户名称</th>
            <th>归属</th>
            <th>类型</th>
            <th class="amount">期初金额</th>
            <th class="amount">余额</th>
            <th>状态</th>
            <th>创建时间</th>
            <th class="actions-head">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(account, index) in accounts" :key="account.id">
            <td class="row-index">{{ index + 1 }}</td>
            <td class="name">
              <input
                v-if="editingId === account.id"
                v-model="draftName"
                type="text"
                maxlength="64"
                :disabled="pendingId !== null"
              />
              <template v-else>{{ account.name }}</template>
            </td>
            <td>
              <span class="badge" :class="account.scope === 'Public' ? 'badge-public' : ''">
                {{ ACCOUNT_SCOPE_LABELS[account.scope] }}
              </span>
              <span v-if="account.ownerUsername" class="owner">{{ account.ownerUsername }}</span>
            </td>
            <td class="type">
              <select
                v-if="editingId === account.id"
                v-model="draftType"
                :disabled="pendingId !== null"
              >
                <option
                  v-for="option in ACCOUNT_TYPE_OPTIONS"
                  :key="option.value"
                  :value="option.value"
                >
                  {{ option.label }}
                </option>
              </select>
              <template v-else>{{ ACCOUNT_TYPE_LABELS[account.type] }}</template>
            </td>
            <!-- 期初金额一经创建不可修改（已落成一笔期初交易），故编辑态下也只读呈现 -->
            <td class="amount">{{ formatAmount(account.initialBalance) }}</td>
            <td class="amount balance">{{ formatAmount(account.balance) }}</td>
            <td>
              <span class="badge" :class="account.isActive ? 'badge-active' : 'badge-inactive'">
                {{ account.isActive ? '已启用' : '已停用' }}
              </span>
            </td>
            <td class="created">{{ formatDateTime(account.createdAt) }}</td>
            <td class="actions">
              <template v-if="editingId === account.id">
                <button
                  type="button"
                  class="ghost"
                  :disabled="pendingId !== null"
                  @click="saveEdit(account)"
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
                  @click="startEdit(account)"
                >
                  编辑
                </button>

                <!-- 停用为软删除，需二次确认；启用无需确认 -->
                <template v-if="account.isActive">
                  <template v-if="confirmingId === account.id">
                    <button
                      type="button"
                      class="danger"
                      :disabled="pendingId !== null"
                      @click="toggleActive(account)"
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
                    @click="confirmingId = account.id"
                  >
                    停用
                  </button>
                </template>
                <button
                  v-else
                  type="button"
                  class="ghost"
                  :disabled="pendingId !== null"
                  @click="toggleActive(account)"
                >
                  启用
                </button>
              </template>
            </td>
          </tr>
          <tr v-if="!accountsStore.loading && accounts.length === 0">
            <td class="empty" colspan="9">
              {{ showInactive ? '暂无账户' : '暂无启用的账户' }}
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </main>
</template>

<style scoped>
.accounts {
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

.create-panel {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 1rem 1.25rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-card);
  background: var(--color-accent-soft);
}

.panel-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-accent-strong);
}

.panel-hint {
  font-size: 12.5px;
  line-height: 1.7;
  opacity: 0.8;
}

.create-row {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 0.5rem;
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

.field input,
.field select,
.table input,
.table select {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  box-shadow: var(--shadow-control);
}

.field input:focus,
.field select:focus,
.table input:focus,
.table select:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 1rem;
}

.toggle {
  display: flex;
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

/* 列表行号：按当前列表顺序连续编号，与账户 Id 无关（停用账户后不会断号） */
.row-index {
  opacity: 0.6;
  white-space: nowrap;
}

.name {
  font-weight: 600;
}

.type {
  white-space: nowrap;
}

/* 金额右对齐并等宽呈现，便于纵向比对 */
.amount {
  text-align: right;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.balance {
  font-weight: 600;
  color: var(--color-accent-strong);
}

.owner {
  margin-left: 0.35rem;
  font-size: 12.5px;
  opacity: 0.7;
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

.badge-public {
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

/* 实心主色按钮：面板中的强视觉锚点（与登录页提交按钮同源） */
.submit {
  align-self: flex-start;
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
