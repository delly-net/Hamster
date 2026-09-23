<script setup lang="ts">
/**
 * 收支记账表单：被「收入」与「支出」两个入口页共用，`mode` 决定记的是哪一种。
 *
 * 两个入口的记账逻辑完全相同（拉账户、校验、提交、提示、时间换算），差异只有标题与提交的类型，
 * 故收敛在一个组件里；两个页面各自是独立文件，使路由切换必然重新挂载——
 * 否则同一组件被两条路由复用时实例会被复用，用户已填的金额与摘要会残留到另一种记账上。
 *
 * 账户候选只取**启用**的账户：停用账户不应再记新账（已停用账户上的历史明细照常可在
 * 「账目明细」页查到，该页用 `includeInactive: true`，口径不同属刻意）。
 * 账本账户不在候选中——它是系统内部账户，后端从不返回它，本组件无需为此写过滤。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import { useAccountSetsStore } from '@/stores/accountSets'
import { useAccountsStore } from '@/stores/accounts'
import {
  useTransactionsStore,
  type RecordableTransactionType,
} from '@/stores/transactions'

const props = defineProps<{
  /** 记账类型：`Income` 收入 / `Expense` 支出。 */
  mode: RecordableTransactionType
}>()

const accountSets = useAccountSetsStore()
const accountsStore = useAccountsStore()
const transactionsStore = useTransactionsStore()

/** `YYYY-MM-DDTHH:mm`，`<input type="datetime-local">` 的原生取值格式。 */
const DATE_TIME_PATTERN = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/

const errorMessage = ref('')
const notice = ref('')

const selectedAccountId = ref<number | string>('')
/** 金额草稿。**声明为 `string`**：输入框用 `type="text"`，v-model 不转型，恒为字符串。 */
const draftAmount = ref('')
const draftOccurredAt = ref('')
const draftSummary = ref('')
const draftRemark = ref('')

/** 是否已选定账套；未选定时只提示、不渲染表单（记账必然落在某个账套内）。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/** 记账类型的中文名，用于按钮与提示文案。 */
const modeLabel = computed(() => (props.mode === 'Income' ? '收入' : '支出'))

/** 账户候选：当前账套内我可见的**启用**账户（已由后端排除账本账户）。 */
const accountOptions = computed(() => accountsStore.accounts)

/** 补零到两位。 */
function pad(value: number): string {
  return String(value).padStart(2, '0')
}

/**
 * 本地时区「此刻」，格式化为 `datetime-local` 的原生取值。
 *
 * **不得**用 `toISOString().slice(0, 16)`：那是 UTC 时刻，东八区会整体前移八小时。
 */
function nowLocalInput(): string {
  const now = new Date()
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}`
}

/**
 * 把 `datetime-local` 的本地时间文本转成 UTC 的 ISO 8601 时刻。
 *
 * 按 `YYYY-MM-DDTHH:mm` 拆段后用 `new Date(y, m - 1, d, h, min)` 构造——**这是本地时间**，
 * 再由 `toISOString()` 折成 UTC。直接 `new Date('2026-09-23T10:00')` 会按 UTC 解释该字符串，
 * 东八区下整体错位八小时。
 *
 * @param value 本地时间文本。
 * @returns UTC 时刻的 ISO 文本；文本不合法时返回 `null`。
 */
function toUtcIso(value: string): string | null {
  const matched = DATE_TIME_PATTERN.exec(value)
  if (matched === null) {
    return null
  }

  const year = Number(matched[1])
  const month = Number(matched[2])
  const day = Number(matched[3])
  const hour = Number(matched[4])
  const minute = Number(matched[5])
  if (![year, month, day, hour, minute].every(Number.isFinite)) {
    return null
  }

  const at = new Date(year, month - 1, day, hour, minute, 0, 0)
  return Number.isNaN(at.getTime()) ? null : at.toISOString()
}

/**
 * 解析金额输入。
 *
 * 先 `String(raw)` 归一入参类型：调用点都绑 `type="text"` 的字符串，但归一让本函数
 * 对任何入参都不抛异常（`AccountView.vue` 曾在 `raw.trim()` 上踩过非字符串入参的坑）。
 * 用 `Number()` + `Number.isFinite` 而非 `parseFloat`：后者对 `12abc` 会悄悄返回 `12`。
 *
 * @param raw 原始输入。
 * @returns 合法且大于 0 时返回金额，否则返回 `null`。
 */
function parseAmount(raw: unknown): number | null {
  const text = String(raw ?? '').trim()
  if (text.length === 0) {
    return null
  }

  const parsed = Number(text)
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return null
  }

  return parsed
}

/** 拉取记账可选的账户（仅启用）。 */
async function loadAccounts(): Promise<boolean> {
  try {
    await accountsStore.list(false)
    return true
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载账户失败'
    return false
  }
}

/** 复位表单：账户回到第一个候选、金额与摘要/备注清空、时间回到此刻。 */
function resetFields(): void {
  selectedAccountId.value = accountOptions.value[0]?.id ?? ''
  draftAmount.value = ''
  draftOccurredAt.value = nowLocalInput()
  draftSummary.value = ''
  draftRemark.value = ''
}

/**
 * 提交记账。
 *
 * 前端先拦下明显不完整的输入、**不发请求**（后端错误已由 `http.ts` 拼成中文，
 * 但让用户为一处空字段等一次往返没有意义）。
 */
async function submit(): Promise<void> {
  if (!hasAccountSet.value) {
    return
  }

  errorMessage.value = ''
  notice.value = ''

  const accountId = Number(selectedAccountId.value)
  if (!Number.isInteger(accountId) || accountId <= 0) {
    errorMessage.value = '请选择账户'
    return
  }

  const amount = parseAmount(draftAmount.value)
  if (amount === null) {
    errorMessage.value = '请填写有效的金额（须大于 0）'
    return
  }

  const occurredAt = toUtcIso(draftOccurredAt.value)
  if (occurredAt === null) {
    errorMessage.value = '请填写有效的发生时间'
    return
  }

  const summary = draftSummary.value.trim()
  if (summary.length === 0) {
    errorMessage.value = '请填写摘要'
    return
  }

  const remark = draftRemark.value.trim()

  try {
    const created = await transactionsStore.record({
      type: props.mode,
      accountId,
      amount,
      occurredAt,
      summary,
      remark: remark.length === 0 ? null : remark,
    })

    // 记账后清空并可立即接着记下一笔：金额与摘要是逐笔的，沿用上一笔只会导致误提交
    resetFields()
    notice.value = `已记录一笔${modeLabel.value}：${created.summary} ${created.accountName}`
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '记账失败'
  }
}

// 账套切换后账户候选随之改变：重拉账户并复位表单，避免把账记到上一账套的账户上。
// 未选择账套时清空，避免退出登录后仍残留可见数据。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    errorMessage.value = ''
    notice.value = ''

    if (currentId === null) {
      accountsStore.clear()
      return
    }

    await loadAccounts()
    resetFields()
  },
)

onMounted(async () => {
  if (!hasAccountSet.value) {
    return
  }

  if (await loadAccounts()) {
    resetFields()
  }
})
</script>

<template>
  <section class="record">
    <!-- 未选定账套：交易必然落在某个账套内，此时不渲染表单 -->
    <p v-if="!hasAccountSet" class="hint">
      当前未选择账套，请先点击右上角的【切换】选择账套后再记一笔{{ modeLabel }}。
    </p>

    <template v-else>
      <form class="form" @submit.prevent="submit">
        <div class="field">
          <label class="label" for="record-account">账户</label>
          <select id="record-account" v-model="selectedAccountId">
            <option v-if="accountOptions.length === 0" value="">当前账套内没有可用账户</option>
            <option v-for="account in accountOptions" :key="account.id" :value="account.id">
              {{ account.name }}
            </option>
          </select>
        </div>

        <div class="field">
          <label class="label" for="record-amount">金额（元）</label>
          <!-- 刻意用 type="text" 而非 type="number"：后者的 v-model 会隐式转型，
               输入有效数字时是 number、清空时是 string，两种类型混在一个 ref 里 -->
          <input
            id="record-amount"
            v-model="draftAmount"
            type="text"
            inputmode="decimal"
            autocomplete="off"
            placeholder="0.00"
          />
        </div>

        <div class="field">
          <label class="label" for="record-occurred-at">发生时间</label>
          <input id="record-occurred-at" v-model="draftOccurredAt" type="datetime-local" />
        </div>

        <div class="field field-wide">
          <label class="label" for="record-summary">摘要</label>
          <input
            id="record-summary"
            v-model="draftSummary"
            type="text"
            maxlength="128"
            autocomplete="off"
            :placeholder="mode === 'Income' ? '如：工资' : '如：午餐'"
          />
        </div>

        <div class="field field-wide">
          <label class="label" for="record-remark">备注（可选）</label>
          <input
            id="record-remark"
            v-model="draftRemark"
            type="text"
            maxlength="256"
            autocomplete="off"
          />
        </div>

        <div class="actions">
          <button type="submit" class="submit" :disabled="transactionsStore.loading">
            {{ transactionsStore.loading ? '保存中…' : `记一笔${modeLabel}` }}
          </button>
          <button
            type="button"
            class="ghost"
            :disabled="transactionsStore.loading"
            @click="resetFields"
          >
            重置
          </button>
        </div>
      </form>

      <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
      <p v-if="notice" class="notice">{{ notice }}</p>

      <p class="hint">
        一笔{{ modeLabel }}会同时记两条明细——所选账户与系统账本账户各一条、金额相等方向相反，
        这正是复式记账的配平方式。账本账户为系统内部账户，不在账户列表与筛选列表中呈现。
        发生时间取业务发生时间，可补记往日的{{ modeLabel }}。
      </p>
    </template>
  </section>
</template>

<style scoped>
.record {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.form {
  display: grid;
  /* 两列自适应：窄屏自动并为一列，无需媒体查询 */
  grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
  gap: 0.85rem;
  padding: 1rem 1.15rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  min-width: 0;
}

/* 摘要与备注占满整行：文案较长，半行放不下 */
.field-wide {
  grid-column: 1 / -1;
}

.label {
  font-size: 12.5px;
  opacity: 0.75;
}

.field input,
.field select {
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
.field select:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

/* 金额等宽呈现，避免输入时数字左右跳动 */
#record-amount {
  font-variant-numeric: tabular-nums;
}

.actions {
  grid-column: 1 / -1;
  display: flex;
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

.ghost,
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

/* 实心主色按钮：与账户页的「新增」、明细页的「查询」同源 */
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

  .submit:not(:disabled):hover {
    border-color: var(--color-accent-strong);
    background: var(--color-accent-strong);
  }
}

.ghost:disabled,
.submit:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
</style>
