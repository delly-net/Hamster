<script setup lang="ts">
/**
 * /admin/account-sets 页面：系统管理员的账套管理台。
 *
 * 两类操作对应后端两组管理端点：账套本身的新增/改名/删除，以及账套与用户的关联维护（多对多）。
 *
 * 关联用户采用**覆盖式**保存：面板中的勾选集合即该账套关联用户的完整目标集合，整体提交。
 *
 * 页面上的禁用态只是体验层的预先提示，后端对同一规则有独立校验，绕过前端只会拿到 4xx。
 */
import { computed, onMounted, ref } from 'vue'
import { ApiError } from '@/api/http'
import { useAccountSetsStore, type AccountSet } from '@/stores/accountSets'
import { useUsersStore } from '@/stores/users'

const accountSetsStore = useAccountSetsStore()
const usersStore = useUsersStore()

/** 后端回传的是带 `Z` 的 UTC 时间，交给 `Intl` 按浏览器本地时区呈现。 */
const dateTimeFormatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'short',
})

const errorMessage = ref('')
const notice = ref('')

/** 新建表单。 */
const newName = ref('')
const newRemark = ref('')
/** 自定义账套 Id 从 1 起自增，`0` 可安全用作「新建表单提交中」的哨兵值。 */
const NEW_ID = 0
/** 正在提交的账套 Id（`NEW_ID` 表示新建）；用于禁用按钮、避免重复提交。 */
const pendingId = ref<number | null>(null)
/** 正在改名/改备注的账套 Id 及其草稿值。 */
const editingId = ref<number | null>(null)
const draftName = ref('')
const draftRemark = ref('')
/** 正在维护关联用户的账套 Id；同一时刻只展开一个面板。 */
const membersId = ref<number | null>(null)
const checkedUserIds = ref<number[]>([])
/** 待二次确认删除的账套 Id。 */
const confirmingDeleteId = ref<number | null>(null)

const accountSets = computed(() => accountSetsStore.adminList)

/** 关联面板展开时才有意义：面板内需要用户清单作为候选。 */
const membersTarget = computed(
  () => accountSets.value.find((item) => item.id === membersId.value) ?? null,
)

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
    await accountSetsStore.listAccountSets()
    return true
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '操作失败，请稍后重试'
    return false
  } finally {
    pendingId.value = null
    confirmingDeleteId.value = null
  }
}

async function load(): Promise<void> {
  errorMessage.value = ''
  try {
    // 关联面板的用户候选来自用户管理接口，一并拉取
    await Promise.all([accountSetsStore.listAccountSets(), usersStore.listUsers()])
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载账套列表失败'
  }
}

/** 新建账套。 */
async function createAccountSet(): Promise<void> {
  const name = newName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '请填写账套名称'
    return
  }

  await run(async () => {
    await accountSetsStore.createAccountSet(name, newRemark.value)
    newName.value = ''
    newRemark.value = ''
    notice.value = `已新建账套 ${name}`
  }, NEW_ID)
}

/** 进入改名/改备注状态。 */
function startEdit(accountSet: AccountSet): void {
  editingId.value = accountSet.id
  draftName.value = accountSet.name
  draftRemark.value = accountSet.remark ?? ''
}

/** 保存改名/改备注。 */
async function saveEdit(accountSet: AccountSet): Promise<void> {
  const name = draftName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '账套名称不能为空'
    return
  }

  const ok = await run(async () => {
    await accountSetsStore.updateAccountSet(accountSet.id, name, draftRemark.value)
    notice.value = `已保存账套 ${name}`
  }, accountSet.id)

  if (ok) {
    editingId.value = null
  }
}

/** 展开/收起关联用户面板；展开时拉取当前已关联的用户。 */
async function toggleMembers(accountSet: AccountSet): Promise<void> {
  if (membersId.value === accountSet.id) {
    membersId.value = null
    return
  }

  errorMessage.value = ''
  try {
    checkedUserIds.value = await accountSetsStore.loadMembers(accountSet.id)
    membersId.value = accountSet.id
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载关联用户失败'
  }
}

/** 勾选/取消勾选一个候选用户。 */
function toggleChecked(userId: number): void {
  checkedUserIds.value = checkedUserIds.value.includes(userId)
    ? checkedUserIds.value.filter((id) => id !== userId)
    : [...checkedUserIds.value, userId]
}

/** 覆盖式保存关联用户。 */
async function saveMembers(): Promise<void> {
  const target = membersTarget.value
  if (target === null) {
    return
  }

  await run(async () => {
    await accountSetsStore.saveMembers(target.id, checkedUserIds.value)
    notice.value = `已更新账套 ${target.name} 的关联用户（共 ${checkedUserIds.value.length} 个）`
  }, target.id)
}

/** 删除账套（需先点一次「删除」再确认）。 */
async function removeAccountSet(accountSet: AccountSet): Promise<void> {
  await run(async () => {
    await accountSetsStore.deleteAccountSet(accountSet.id)
    notice.value = `已删除账套 ${accountSet.name}`

    if (membersId.value === accountSet.id) {
      membersId.value = null
    }
  }, accountSet.id)
}

onMounted(load)
</script>

<template>
  <main class="admin">
    <header class="head">
      <div>
        <h1 class="title">账套管理</h1>
        <p class="subtitle">
          账套与用户为多对多关系，被关联的用户登录后可在账套间切换；管理员无需关联即可访问全部账套。
        </p>
      </div>
      <button type="button" class="ghost" :disabled="accountSetsStore.adminLoading" @click="load">
        {{ accountSetsStore.adminLoading ? '刷新中…' : '刷新' }}
      </button>
    </header>

    <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
    <p v-if="notice" class="notice">{{ notice }}</p>

    <section class="create-panel">
      <h2 class="panel-title">新建账套</h2>
      <div class="create-row">
        <label class="field">
          <span class="label">名称</span>
          <input
            v-model="newName"
            type="text"
            maxlength="64"
            placeholder="必填，全局唯一"
            :disabled="pendingId !== null"
          />
        </label>
        <label class="field field-grow">
          <span class="label">备注</span>
          <input
            v-model="newRemark"
            type="text"
            maxlength="256"
            placeholder="可选，用于说明账套用途"
            :disabled="pendingId !== null"
          />
        </label>
        <button
          type="button"
          class="submit"
          :disabled="pendingId !== null"
          @click="createAccountSet"
        >
          {{ pendingId === NEW_ID ? '创建中…' : '创建' }}
        </button>
      </div>
    </section>

    <section v-if="membersTarget" class="members-panel">
      <h2 class="panel-title">账套 {{ membersTarget.name }} 的关联用户</h2>
      <p class="panel-hint">
        勾选即该账套关联用户的完整集合，保存后整体覆盖；管理员无需勾选即可访问全部账套。
      </p>

      <div v-if="usersStore.users.length > 0" class="members-list">
        <label v-for="user in usersStore.users" :key="user.id" class="member">
          <input
            type="checkbox"
            :checked="checkedUserIds.includes(user.id)"
            :disabled="pendingId !== null"
            @change="toggleChecked(user.id)"
          />
          <span class="member-name">{{ user.username }}</span>
          <span v-if="user.isAdmin" class="badge badge-admin">系统管理员</span>
          <span v-if="!user.isActive" class="badge badge-inactive">未激活</span>
        </label>
      </div>
      <p v-else class="panel-hint">暂无可关联的用户</p>

      <div class="members-actions">
        <button type="button" class="submit" :disabled="pendingId !== null" @click="saveMembers">
          {{
            pendingId === membersTarget.id
              ? '保存中…'
              : `保存关联（已选 ${checkedUserIds.length} 个）`
          }}
        </button>
        <button
          type="button"
          class="ghost"
          :disabled="pendingId !== null"
          @click="membersId = null"
        >
          关闭
        </button>
      </div>
    </section>

    <table class="table">
      <thead>
        <tr>
          <th>序号</th>
          <th>账套名称</th>
          <th>备注</th>
          <th>关联用户</th>
          <th>创建时间</th>
          <th class="actions-head">操作</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(accountSet, index) in accountSets" :key="accountSet.id">
          <td class="row-index">{{ index + 1 }}</td>
          <td class="name">
            <input
              v-if="editingId === accountSet.id"
              v-model="draftName"
              type="text"
              maxlength="64"
              :disabled="pendingId !== null"
            />
            <template v-else>{{ accountSet.name }}</template>
          </td>
          <td class="remark">
            <input
              v-if="editingId === accountSet.id"
              v-model="draftRemark"
              type="text"
              maxlength="256"
              :disabled="pendingId !== null"
            />
            <template v-else>{{ accountSet.remark ?? '—' }}</template>
          </td>
          <td>
            <span class="badge badge-count">{{ accountSet.memberCount ?? 0 }} 个</span>
          </td>
          <td class="created">{{ formatDateTime(accountSet.createdAt) }}</td>
          <td class="actions">
            <template v-if="editingId === accountSet.id">
              <button
                type="button"
                class="ghost"
                :disabled="pendingId !== null"
                @click="saveEdit(accountSet)"
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
                @click="startEdit(accountSet)"
              >
                编辑
              </button>
              <button
                type="button"
                class="ghost"
                :disabled="pendingId !== null"
                @click="toggleMembers(accountSet)"
              >
                {{ membersId === accountSet.id ? '收起用户' : '关联用户' }}
              </button>
            </template>

            <template v-if="confirmingDeleteId === accountSet.id">
              <button
                type="button"
                class="danger"
                :disabled="pendingId !== null"
                @click="removeAccountSet(accountSet)"
              >
                确认删除
              </button>
              <button
                type="button"
                class="ghost"
                :disabled="pendingId !== null"
                @click="confirmingDeleteId = null"
              >
                取消
              </button>
            </template>
            <button
              v-else
              type="button"
              class="danger"
              :disabled="pendingId !== null"
              @click="confirmingDeleteId = accountSet.id"
            >
              删除
            </button>
          </td>
        </tr>
        <tr v-if="!accountSetsStore.adminLoading && accountSets.length === 0">
          <td class="empty" colspan="6">暂无账套</td>
        </tr>
      </tbody>
    </table>
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

.create-panel,
.members-panel {
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
  min-width: 12rem;
}

.field-grow {
  flex: 1;
}

.label {
  font-size: 12.5px;
  opacity: 0.75;
}

.field input,
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

.field input:focus,
.table input:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

.members-list {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem 1rem;
  max-height: 14rem;
  overflow-y: auto;
  padding: 0.5rem 0;
}

.member {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 13.5px;
}

.member-name {
  font-weight: 600;
}

.members-actions {
  display: flex;
  gap: 0.5rem;
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

/* 列表行号：按当前列表顺序连续编号，与账套 Id 无关（删除账套后不会断号） */
.row-index {
  opacity: 0.6;
  white-space: nowrap;
}

.name {
  font-weight: 600;
}

.remark {
  max-width: 20rem;
  opacity: 0.8;
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

.badge-admin {
  border-color: var(--color-accent);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
}

.badge-count {
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
