<script setup lang="ts">
/**
 * 记账表单：被「收入」「支出」「转账」三个入口页共用，`mode` 决定记的是哪一种。
 *
 * 三个入口的记账逻辑完全相同（拉币种与账户、校验、提交、提示、时间换算），差异只有几处文案、
 * 账户候选范围与「对手方是否必填」，故收敛在一个组件里；三个页面各自是独立文件，
 * 使路由切换必然重新挂载——否则同一组件被多条路由复用时实例会被复用，
 * 用户已填的金额与摘要会残留到另一种记账上。
 *
 * 颜色也随类型走：根元素标 `data-entry-mode`，`base.css` 的类型主题色别名层据此把
 * `--entry-color*`（绿/红/蓝）折好，提交按钮与成功提示直接取用。故本组件没有一处
 * 「哪种 mode 用哪种颜色」的分支——颜色定义只有一个地方，就是 `base.css`。
 *
 * 表单按「币种 → 主账户 → 对手方账户」的顺序自上而下填写，三者是联动的：
 * - **币种在最前**：它是其余字段的筛选条件，先定币种才能给出该币种的账户候选。
 *   换币种会清空两个账户选择——留着上一个币种选好的账户，提交必然撞上后端的跨币种校验。
 * - **主账户**（收入账户 / 支出账户 / 转出账户）：必须从候选中选定，**不接受不存在的名字**
 *   （`freeText: false`）——记到不存在的账户上没有意义。
 * - **对手方账户**（来源账户 / 目标账户 / 转入账户）：可留空，也可输入一个不存在的名字
 *   （`freeText: true`）。留空表示「款项来自/去往账套之外」，后端会落到该币种的系统账本账户；
 *   填了不存在的名字则由后端自动创建为个人往来账户。
 * - **分类**：可留空（即「未分类」），也可输入一个不存在的名字——后端会在当前账套内自动创建它。
 *   它与币种无关（换币种不清空），也不区分收入/支出/转账，故不参与上面那套联动。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import AccountSearchSelect from '@/components/AccountSearchSelect.vue'
import CategorySearchSelect from '@/components/CategorySearchSelect.vue'
import { useAccountSetsStore } from '@/stores/accountSets'
import { TRANSFER_ACCOUNT_TYPES, useAccountsStore } from '@/stores/accounts'
import { useCategoriesStore } from '@/stores/categories'
import { useCurrenciesStore } from '@/stores/currencies'
import { useTransactionsStore, type RecordableTransactionType } from '@/stores/transactions'

const props = defineProps<{
  /** 记账类型：`Income` 收入 / `Expense` 支出 / `Transfer` 转账。 */
  mode: RecordableTransactionType
}>()

/** 是否在记一笔转账。转账的几处差异都由它分支。 */
const isTransfer = computed(() => props.mode === 'Transfer')

/**
 * 各记账类型的文案与账户角色。
 *
 * 用查找表而非嵌套三元表达式：类型从两种涨到三种后，三元表达式会退化成
 * 「A ? x : B ? y : z」这类读不出对应关系的式子，而这张表把「哪种记账用哪套词」摊平了，
 * 新增类型只需加一行、漏加时 TypeScript 会因 `Record` 缺键当场报错。
 */
const MODE_META: Record<
  RecordableTransactionType,
  {
    /** 记账类型的中文名，用于按钮与提示文案。 */
    label: string
    /** 主账户的字段名。 */
    primaryLabel: string
    /** 对手方账户的字段名。 */
    counterpartyLabel: string
    /** 摘要输入框的占位示例。 */
    summaryPlaceholder: string
  }
> = {
  Income: {
    label: '收入',
    primaryLabel: '收入账户',
    counterpartyLabel: '来源账户',
    summaryPlaceholder: '如：工资',
  },
  Expense: {
    label: '支出',
    primaryLabel: '支出账户',
    counterpartyLabel: '目标账户',
    summaryPlaceholder: '如：午餐',
  },
  Transfer: {
    label: '转账',
    primaryLabel: '转出账户',
    counterpartyLabel: '转入账户',
    summaryPlaceholder: '如：还信用卡',
  },
}

/** 当前记账类型的文案与账户角色。 */
const meta = computed(() => MODE_META[props.mode])

const accountSets = useAccountSetsStore()
const accountsStore = useAccountsStore()
const currenciesStore = useCurrenciesStore()
const categoriesStore = useCategoriesStore()
const transactionsStore = useTransactionsStore()

/** `YYYY-MM-DDTHH:mm`，`<input type="datetime-local">` 的原生取值格式。 */
const DATE_TIME_PATTERN = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/

/** 分类名最大长度，与后端 `Category.Name` 的列长一致。前端拦一道只是为了少一次往返。 */
const CATEGORY_NAME_MAX_LENGTH = 32

const errorMessage = ref('')
const notice = ref('')

/** 选定的币种代码；为空串表示币种字典尚未加载或一个币种都没有。 */
const selectedCurrencyCode = ref('')

/** 主账户：文本与主键分开持有，理由见 `AccountSearchSelect` 的说明。 */
const primaryAccountId = ref<number | null>(null)
const primaryAccountText = ref('')

/** 对手方账户（收入页为「来源账户」，支出页为「目标账户」）。 */
const counterpartyAccountId = ref<number | null>(null)
const counterpartyAccountText = ref('')

/**
 * 分类：可留空（即「未分类」，后端接受），也可输入一个尚不存在的名字。
 *
 * 与账户同样的两个 ref：从候选中点选时 `id` 有值，手工输入新名字时只有文本有值——
 * 那个名字会被后端自动创建为分类。分类**与币种无关**，故换币种时不随之清空。
 */
const categoryId = ref<number | null>(null)
const categoryText = ref('')

/** 金额草稿。**声明为 `string`**：输入框用 `type="text"`，v-model 不转型，恒为字符串。 */
const draftAmount = ref('')
const draftOccurredAt = ref('')
const draftSummary = ref('')
const draftRemark = ref('')

/** 是否已选定账套；未选定时只提示、不渲染表单（记账必然落在某个账套内）。 */
const hasAccountSet = computed(() => accountSets.currentId !== null)

/** 记账类型的中文名，用于按钮与提示文案。 */
const modeLabel = computed(() => meta.value.label)

/**
 * 主账户的字段名：收入页叫「收入账户」，支出页叫「支出账户」，转账页叫「转出账户」。
 *
 * 名字随方向变而非统一叫「账户」：用户看到「收入账户」就知道这里是钱的**落点**，
 * 看到「转出账户」就知道这里是钱的**来处**，两个框的分工无需额外解释。
 */
const primaryLabel = computed(() => meta.value.primaryLabel)

/** 对手方账户的字段名：收入页为「来源账户」，支出页为「目标账户」，转账页为「转入账户」。 */
const counterpartyLabel = computed(() => meta.value.counterpartyLabel)

/** 可用币种；一个都没有时表单无从填起。 */
const currencyOptions = computed(() => currenciesStore.currencies)

/**
 * 账户候选：当前账套内我可见的**启用**账户，且**币种与所选币种一致**；
 * 转账时**还要求类型是资金账户或负债账户**。
 *
 * 两处过滤都是体验层的提前收敛，不是防线：真正的约束是后端的跨币种校验与
 * `AccountTypeExtensions.IsTransferAccount` 校验。不过滤的话，用户会先选中一个
 * 别的币种或往来类型的账户，提交时才被 400 挡下。
 */
const accountOptions = computed(() =>
  accountsStore.accounts.filter(
    (account) =>
      account.currencyCode === selectedCurrencyCode.value &&
      (!isTransfer.value || TRANSFER_ACCOUNT_TYPES.includes(account.type)),
  ),
)

/**
 * 分类候选：当前账套内的**启用**分类（停用的不再供新记账选择）。
 *
 * 不做任何本地过滤——分类没有可见性维度，也不随币种变化，账套内的全量启用分类即是候选全集。
 */
const categoryOptions = computed(() => categoriesStore.categories)

/** 分类字段的提示文案。 */
const categoryPlaceholder = computed(() =>
  categoryOptions.value.length === 0 ? '暂无分类，可直接输入新分类名' : '可留空，或输入新分类名',
)

/** 主账户字段的提示文案。 */
const primaryPlaceholder = computed(() =>
  accountOptions.value.length === 0 ? `当前币种下没有可用账户` : '输入关键词筛选，从候选中选择',
)

/**
 * 对手方账户字段的提示文案。
 *
 * 转账下不再是「可留空」——两个真实账户之间才有转账，没有「账套之外」这一说。
 */
const counterpartyPlaceholder = computed(() =>
  isTransfer.value
    ? accountOptions.value.length === 0
      ? '当前币种下没有可用账户'
      : '输入关键词筛选，从候选中选择'
    : '留空即账本账户（账套之外）',
)

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

/** 拉取币种字典、记账可选的账户与分类（三者都只要启用的）。 */
async function loadOptions(): Promise<boolean> {
  try {
    // 并发拉取：三者互不依赖（账户的币种过滤在前端做），串行只是白白多等几次往返
    await Promise.all([
      currenciesStore.loadActive(),
      accountsStore.list(false),
      categoriesStore.list(false),
    ])
    return true
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载币种、账户与分类失败'
    return false
  }
}

/**
 * 复位表单：币种回到默认币种、主账户回到该币种下的第一个候选、对手方清空、时间回到此刻。
 *
 * 主账户预填第一个候选而非留空——记账是高频动作，绝大多数时候提交的就是默认那个账户。
 */
function resetFields(): void {
  selectedCurrencyCode.value = currenciesStore.defaultCode ?? currencyOptions.value[0]?.code ?? ''

  const first = accountOptions.value[0] ?? null
  primaryAccountId.value = first?.id ?? null
  primaryAccountText.value = first?.name ?? ''

  counterpartyAccountId.value = null
  counterpartyAccountText.value = ''

  // 分类每笔重填：它是逐笔的语义（这一笔因何而发生），沿用上一笔与沿用上一笔金额同样危险
  categoryId.value = null
  categoryText.value = ''

  draftAmount.value = ''
  draftOccurredAt.value = nowLocalInput()
  draftSummary.value = ''
  draftRemark.value = ''
}

/**
 * 换币种：先落定新币种，再清空两个账户选择。
 *
 * 上一个币种下选好的账户在新币种里根本不在候选中，留着它提交必然撞上后端的跨币种校验，
 * 不如当场清掉，让用户在新币种的候选里重选。
 *
 * 用 `:value` + `@change` 而非 `v-model` + `@change`：后者两个监听器的执行顺序不确定，
 * 而这里「先更新币种、再按新币种清空」是有序的。
 */
function onCurrencyChange(event: Event): void {
  selectedCurrencyCode.value = (event.target as HTMLSelectElement).value

  primaryAccountId.value = null
  primaryAccountText.value = ''
  counterpartyAccountId.value = null
  counterpartyAccountText.value = ''
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

  const currencyCode = selectedCurrencyCode.value
  if (currencyCode.length === 0) {
    errorMessage.value = '请选择币种'
    return
  }

  const accountId = primaryAccountId.value
  if (accountId === null) {
    errorMessage.value = `请选择${primaryLabel.value}`
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

  // 分类**可留空**（即「未分类」，后端接受）；填了名字就先在本机拦住超长，
  // 与后端 `Category.Name` 同一列长——让用户立刻看到问题，而不必为一处超长等一次往返
  const categoryNameDraft = categoryText.value.trim()
  if (categoryNameDraft.length > CATEGORY_NAME_MAX_LENGTH) {
    errorMessage.value = `分类名不能超过 ${CATEGORY_NAME_MAX_LENGTH} 位`
    return
  }

  // 转账的两个端点都是真实账户，没有「账套之外」这一说，故转入账户必须选定（后端同样会拒）
  if (isTransfer.value && counterpartyAccountId.value === null) {
    errorMessage.value = `请选择${counterpartyLabel.value}`
    return
  }

  // 收支的对手方可留空：留空即「款项来自/去往账套之外」，由后端落到该币种的系统账本账户。
  // 与主账户重合时当场拦下——同一账户自转自的账是两条明细相互抵消的空交易，记了等于没记。
  if (counterpartyAccountId.value !== null && counterpartyAccountId.value === accountId) {
    errorMessage.value = `${counterpartyLabel.value}不能与${primaryLabel.value}是同一个账户`
    return
  }

  // 两者至多传一个：从候选中选定后用户又改了名字，此时子组件已按新文本清空了 id，
  // 故此处按「有 id 传 id、否则传名字」取值即可，不会同时送出两个互相矛盾的字段
  const counterpartyId = counterpartyAccountId.value
  const counterpartyName = counterpartyAccountText.value.trim()

  try {
    const created = await transactionsStore.record({
      type: props.mode,
      accountId,
      amount,
      occurredAt,
      summary,
      remark: remark.length === 0 ? null : remark,
      currencyCode,
      counterpartyAccountId: counterpartyId,
      // 转账恒不传名字（后端拒绝非空取值）：上面已保证此时 id 非空，这一层是防止
      // 将来放宽「转入账户必填」时，这里会悄悄开始按名创建往来账户、绕开账户类型限制
      counterpartyName:
        !isTransfer.value && counterpartyId === null && counterpartyName.length > 0
          ? counterpartyName
          : null,
      // 分类同为「有 id 传 id、否则传名字」；两者皆空即「未分类」，这是合法的，后端不报错。
      // 与对手方不同的是：分类**永远接受按名创建**，转账也不例外——分类没有账户类型那样的限制
      categoryId: categoryId.value,
      categoryName:
        categoryId.value === null && categoryNameDraft.length > 0 ? categoryNameDraft : null,
    })

    // 记账后清空并可立即接着记下一笔：金额与摘要是逐笔的，沿用上一笔只会导致误提交
    resetFields()
    // 转账提示读作「转出 A → 转入 B」：两个账户都是用户自己选的，方向和起止点必须一眼看清；
    // 收支则用「主账户（对手方）」的写法，对手方留空时呈现后端落的系统账本账户
    // 分类回显的是**后端落定的那个分类**（本次手工输入的名字可能是刚被自动创建的），
    // 而不是输入框里的文本：只有后端才知道这个名字最终归到了哪一条记录上。
    // 未分类时不显示这一段——空括号不如什么都不要
    const categorySuffix = created.categoryName === null ? '' : ` · 分类：${created.categoryName}`

    notice.value = isTransfer.value
      ? `已记录一笔转账：${created.summary} 转出 ${created.accountName} → 转入 ${created.counterpartyName ?? '—'}${categorySuffix}`
      : `已记录一笔${modeLabel.value}：${created.summary} ${created.accountName}` +
        `（${counterpartyLabel.value}：${created.counterpartyName ?? '账本账户'}）${categorySuffix}`
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '记账失败'
  }
}

// 账套切换后账户候选随之改变：重拉账户并复位表单，避免把账记到上一账套的账户上。
// 未选择账套时清空，避免退出登录后仍残留可见数据（币种字典虽是全局的，也一并清掉）。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    errorMessage.value = ''
    notice.value = ''

    if (currentId === null) {
      accountsStore.clear()
      currenciesStore.clear()
      categoriesStore.clear()
      return
    }

    if (await loadOptions()) {
      resetFields()
    }
  },
)

onMounted(async () => {
  if (!hasAccountSet.value) {
    return
  }

  if (await loadOptions()) {
    resetFields()
  }
})
</script>

<template>
  <section class="record" :data-entry-mode="mode">
    <!-- 未选定账套：交易必然落在某个账套内，此时不渲染表单 -->
    <p v-if="!hasAccountSet" class="hint">
      当前未选择账套，请先点击右上角的【切换】选择账套后再记一笔{{ modeLabel }}。
    </p>

    <template v-else>
      <form class="form" @submit.prevent="submit">
        <div class="field">
          <label class="label" for="record-currency">币种</label>
          <select id="record-currency" :value="selectedCurrencyCode" @change="onCurrencyChange">
            <option v-if="currencyOptions.length === 0" value="">暂无可用币种</option>
            <option v-for="currency in currencyOptions" :key="currency.id" :value="currency.code">
              {{ currency.code }} {{ currency.name }}
            </option>
          </select>
        </div>

        <div class="field">
          <label class="label" for="record-account">{{ primaryLabel }}</label>
          <AccountSearchSelect
            v-model:id="primaryAccountId"
            v-model:text="primaryAccountText"
            input-id="record-account"
            :options="accountOptions"
            :placeholder="primaryPlaceholder"
            :free-text="false"
          />
        </div>

        <div class="field">
          <label class="label" for="record-counterparty">{{ counterpartyLabel }}</label>
          <!-- 转账的转入账户必须从候选中选定（freeText: false）：转账两端都是真实账户，
               不存在的名字会被后端创建为往来账户，而转账只允许资金与负债账户 -->
          <AccountSearchSelect
            v-model:id="counterpartyAccountId"
            v-model:text="counterpartyAccountText"
            input-id="record-counterparty"
            :options="accountOptions"
            :placeholder="counterpartyPlaceholder"
            :free-text="!isTransfer"
          />
        </div>

        <div class="field field-wide">
          <label class="label" for="record-category">分类（可选）</label>
          <!-- 分类可留空（即「未分类」），也可直接输入一个尚不存在的名字——后端会为它自动创建分类。
               与账户选择框的分工一致：从候选中点选时上报 id，手工输入时上报名字 -->
          <CategorySearchSelect
            v-model:id="categoryId"
            v-model:text="categoryText"
            input-id="record-category"
            :options="categoryOptions"
            :placeholder="categoryPlaceholder"
          />
        </div>

        <div class="field">
          <label class="label" for="record-amount">金额（{{ selectedCurrencyCode || '—' }}）</label>
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
            :placeholder="meta.summaryPlaceholder"
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

      <p v-if="isTransfer" class="hint">
        一笔转账会同时记两条明细——{{ primaryLabel }}与{{ counterpartyLabel }}各一条、
        金额相等方向相反，这正是复式记账的配平方式。两个账户都必须从候选中选定，
        且只允许资金账户与负债账户：往来账户记的是「谁欠谁」而不是「钱放在哪」，
        钱转进转出它并不改变钱的所在。转账也不支持跨币种，只有币种相同的账户之间才能转账。
        发生时间取业务发生时间，可补记往日的转账。
        <strong>分类可以留空</strong>：它是「这笔账因何而发生」，一笔转账只带一个分类；
        填入一个尚不存在的分类名时，会为你在当前账套内自动创建这个分类。
      </p>

      <p v-else class="hint">
        一笔{{ modeLabel }}会同时记两条明细——{{ primaryLabel }}与{{ counterpartyLabel }}各一条、
        金额相等方向相反，这正是复式记账的配平方式。{{ counterpartyLabel }}可以留空，
        留空即表示款项来自或去往本账套之外（记入系统账本账户，该账户不在任何列表与筛选器中呈现）；
        填入一个尚不存在的账户名时，会为你自动创建一个个人往来账户。只有**币种相同**的账户之间才能记账。
        发生时间取业务发生时间，可补记往日的{{ modeLabel }}。
        <strong>分类可以留空</strong>（留空即「未分类」），也可以直接输入一个新名字——
        它会在当前账套内被自动创建；分类不区分收入/支出/转账，同一份字典三类共用。
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

/* 提示色随记账类型走：收/支/转账各有自己的语义色（见 base.css 的类型主题色别名层），
   再复用主色会让「刚记了一笔支出」的确认框与「记了一笔收入」长得一模一样 */
.notice {
  border: 1px solid var(--entry-color, var(--color-accent));
  background: var(--entry-color-soft, var(--color-accent-soft));
  color: var(--entry-color-strong, var(--color-accent-strong));
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

/* 实心按钮，但**按记账类型着色**而非品牌主色：绿=收入 / 红=支出 / 蓝=转账。
   颜色经 --entry-color 由 base.css 的类型主题色别名层提供（根元素已标 data-entry-mode），
   故三个页面共用同一个组件、样式里却没有一处 mode 分支；
   文字色仍取 --color-accent-contrast（亮色白字 / 暗色深棕字），三个色值下对比度均已核对。 */
.submit {
  display: inline-flex;
  align-items: center;
  padding: 0.5rem 1rem;
  border: 1px solid var(--entry-color, var(--color-accent));
  background: var(--entry-color, var(--color-accent));
  color: var(--color-accent-contrast);
  font-size: 13px;
  font-weight: 600;
}

@media (hover: hover) {
  .ghost:not(:disabled):hover {
    background-color: var(--color-accent-soft);
  }

  /* hover 用显式的 -strong 令牌而非 filter/color-mix：亮色下要更深、暗色下要更亮，
     同一段 CSS 算不出方向相反的两个结果 */
  .submit:not(:disabled):hover {
    border-color: var(--entry-color-strong, var(--color-accent-strong));
    background: var(--entry-color-strong, var(--color-accent-strong));
  }
}

.ghost:disabled,
.submit:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
</style>
