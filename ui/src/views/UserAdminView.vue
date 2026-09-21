<script setup lang="ts">
/**
 * /admin/users 页面：系统管理员的用户管理台。
 *
 * 三类操作对应后端三个管理端点：激活/停用（双向开关）、生成 15 分钟一次性重置链接、删除。
 *
 * 页面上的禁用态（如「不能停用/删除自己」）只是体验层的预先提示，
 * 后端对同一规则有独立校验，绕过前端只会拿到 400。
 */
import { computed, onMounted, ref } from 'vue'
import { ApiError } from '@/api/http'
import { useAuthStore } from '@/stores/auth'
import { useUsersStore, type AdminUser } from '@/stores/users'

const auth = useAuthStore()
const usersStore = useUsersStore()

/** 后端回传的是带 `Z` 的 UTC 时间，交给 `Intl` 按浏览器本地时区呈现。 */
const dateTimeFormatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'short',
})

const errorMessage = ref('')
const notice = ref('')
/** 正在执行操作的用户 Id，用于禁用该行按钮、避免重复提交。 */
const pendingId = ref<number | null>(null)
/** 待二次确认删除的用户 Id。 */
const confirmingDeleteId = ref<number | null>(null)
/** 刚生成的重置链接；同一时刻只保留最近一条。 */
const resetLink = ref<{ username: string; url: string; expiresAt: string } | null>(null)
const linkCopied = ref(false)

const users = computed(() => usersStore.users)

/** 格式化 ISO 时间；无法解析时原样回显。 */
function formatDateTime(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : dateTimeFormatter.format(parsed)
}

/** 当前登录账号自身的 Id：其停用与删除按钮在后端会被拒，这里一并置灰。 */
const selfId = computed(() => auth.user?.id ?? null)

async function load(): Promise<void> {
  errorMessage.value = ''
  try {
    await usersStore.listUsers()
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载用户列表失败'
  }
}

/** 统一包装行内操作：维护 pending 态、刷新列表、呈现失败原因。 */
async function runOnRow(user: AdminUser, action: () => Promise<void>): Promise<void> {
  if (pendingId.value !== null) {
    return
  }

  pendingId.value = user.id
  errorMessage.value = ''
  notice.value = ''

  try {
    await action()
    await usersStore.listUsers()
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '操作失败，请稍后重试'
  } finally {
    pendingId.value = null
    confirmingDeleteId.value = null
  }
}

/** 启用或停用用户。 */
async function toggleActive(user: AdminUser): Promise<void> {
  const nextActive = !user.isActive

  await runOnRow(user, async () => {
    await usersStore.setActive(user.id, nextActive)
    notice.value = `已${nextActive ? '激活' : '停用'}用户 ${user.username}`
  })
}

/** 生成重置链接并展开展示面板。 */
async function generateResetLink(user: AdminUser): Promise<void> {
  resetLink.value = null
  linkCopied.value = false

  await runOnRow(user, async () => {
    const link = await usersStore.createResetLink(user.id)
    resetLink.value = { username: user.username, url: link.resetUrl, expiresAt: link.expiresAt }
  })
}

/** 删除用户（需先点一次「删除」再确认）。 */
async function removeUser(user: AdminUser): Promise<void> {
  await runOnRow(user, async () => {
    await usersStore.deleteUser(user.id)
    notice.value = `已删除用户 ${user.username}`

    // 该用户的链接随之失效，避免面板继续展示已作废的地址
    if (resetLink.value?.username === user.username) {
      resetLink.value = null
    }
  })
}

/** 复制重置链接。 */
async function copyResetLink(): Promise<void> {
  if (resetLink.value === null) {
    return
  }

  try {
    await navigator.clipboard.writeText(resetLink.value.url)
    linkCopied.value = true
    setTimeout(() => (linkCopied.value = false), 1500)
  } catch {
    errorMessage.value = '复制失败：当前浏览器不允许访问剪贴板，请手动选中复制。'
  }
}

onMounted(load)
</script>

<template>
  <main class="admin">
    <header class="head">
      <div>
        <h1 class="title">用户管理</h1>
        <p class="subtitle">
          新注册账号默认未激活，需在此激活后方可登录；密码重置链接有效期 15 分钟且仅可使用一次。
        </p>
      </div>
      <button type="button" class="ghost" :disabled="usersStore.loading" @click="load">
        {{ usersStore.loading ? '刷新中…' : '刷新' }}
      </button>
    </header>

    <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
    <p v-if="notice" class="notice">{{ notice }}</p>

    <section v-if="resetLink" class="link-panel">
      <h2 class="panel-title">用户 {{ resetLink.username }} 的重置链接</h2>
      <p class="panel-hint">
        有效期至 {{ formatDateTime(resetLink.expiresAt) }}，仅可使用一次；重新生成会使本链接立即失效。
      </p>
      <div class="link-row">
        <input class="link-input" type="text" readonly :value="resetLink.url" />
        <button type="button" class="ghost" @click="copyResetLink">
          {{ linkCopied ? '已复制' : '复制' }}
        </button>
        <button type="button" class="ghost" @click="resetLink = null">关闭</button>
      </div>
    </section>

    <table class="table">
      <thead>
        <tr>
          <th>序号</th>
          <th>用户名</th>
          <th>角色</th>
          <th>状态</th>
          <th>注册时间</th>
          <th class="actions-head">操作</th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="(user, index) in users"
          :key="user.id"
          :class="{ 'row-self': user.id === selfId }"
        >
          <td class="row-index">{{ index + 1 }}</td>
          <td class="username">
            {{ user.username }}
            <span v-if="user.id === selfId" class="self-tag">当前账号</span>
          </td>
          <td>
            <span class="badge" :class="user.isAdmin ? 'badge-admin' : 'badge-plain'">
              {{ user.isAdmin ? '系统管理员' : '普通用户' }}
            </span>
          </td>
          <td>
            <span class="badge" :class="user.isActive ? 'badge-active' : 'badge-inactive'">
              {{ user.isActive ? '已激活' : '未激活' }}
            </span>
          </td>
          <td class="created">{{ formatDateTime(user.createdAt) }}</td>
          <td class="actions">
            <button
              type="button"
              class="ghost"
              :disabled="pendingId !== null || (user.isActive && user.id === selfId)"
              :title="user.isActive && user.id === selfId ? '不能停用当前登录账号' : ''"
              @click="toggleActive(user)"
            >
              {{ user.isActive ? '停用' : '激活' }}
            </button>
            <button
              type="button"
              class="ghost"
              :disabled="pendingId !== null"
              @click="generateResetLink(user)"
            >
              重置链接
            </button>
            <template v-if="confirmingDeleteId === user.id">
              <button type="button" class="danger" :disabled="pendingId !== null" @click="removeUser(user)">
                确认删除
              </button>
              <button type="button" class="ghost" :disabled="pendingId !== null" @click="confirmingDeleteId = null">
                取消
              </button>
            </template>
            <button
              v-else
              type="button"
              class="danger"
              :disabled="pendingId !== null || user.id === selfId"
              :title="user.id === selfId ? '不能删除当前登录账号' : ''"
              @click="confirmingDeleteId = user.id"
            >
              删除
            </button>
          </td>
        </tr>
        <tr v-if="!usersStore.loading && users.length === 0">
          <td class="empty" colspan="6">暂无用户</td>
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

.link-panel {
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

.link-row {
  display: flex;
  gap: 0.5rem;
  align-items: center;
}

.link-input {
  flex: 1;
  min-width: 0;
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 12.5px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  box-shadow: var(--shadow-control);
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

.row-self {
  background: var(--color-accent-soft);
}

/* 列表行号：按当前列表顺序连续编号，与用户 Id 无关（删除用户后不会断号） */
.row-index {
  opacity: 0.6;
  white-space: nowrap;
}

.username {
  font-weight: 600;
}

.self-tag {
  margin-left: 0.4rem;
  padding: 0.1rem 0.4rem;
  border: 1px solid var(--color-accent);
  border-radius: 999px;
  color: var(--color-accent-strong);
  font-size: 11px;
  font-weight: 400;
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

.badge-plain {
  border-color: var(--color-border-hover);
  opacity: 0.75;
}

.badge-active {
  border-color: var(--color-accent);
  color: var(--color-accent-strong);
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
.danger {
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

@media (hover: hover) {
  .ghost:not(:disabled):hover {
    background-color: var(--color-accent-soft);
  }

  .danger:not(:disabled):hover {
    background-color: var(--color-danger-soft);
  }
}

.ghost:disabled,
.danger:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
</style>
