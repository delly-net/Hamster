<script setup lang="ts">
/**
 * /accounts 页面：当前账套内的账户管理。
 *
 * 账户一律挂在账套下，故页面严格跟随「当前账套」：未选择账套时只提示、不渲染页签与表格，
 * 账套切换时重新拉取（否则会残留上一账套的账户）。
 *
 * 页面上「个人账户只显示自己的」「公共账户人人可见」等现象都是后端判定的结果，
 * 本页不做任何本地过滤——绕过前端只会拿到 403 / 404。
 *
 * 账本账户（系统在期初入账时自动创建）同样由后端过滤：接口不返回它、也不接受手工新建它，
 * 故本页既看不到它、也无法造出它，前端无需为此写任何过滤或禁用逻辑。页签只有三个，也源于此。
 *
 * 列表按账户类型分页签呈现，**页签是纯呈现层的分组**：接口一次就返回当前账套内我可见的全部类型，
 * 切换页签只是换一个 computed，不重新请求。这与上面「不做本地过滤」并不矛盾——后者针对的是
 * 可见性/权限（前端过滤只会造出假防线），而按类型分组是展示分组，一行数据都没被丢掉。
 *
 * 新建与修改统一走弹窗（【新增】/【编辑】→ 表单 →【保存】落表），不再有行内编辑。
 * 修改的可编辑字段只有名称：账户类型、归属范围、期初金额与**币种**一经创建均不可修改，
 * 故在弹窗内一律只读呈现（不给可编辑控件，连禁用的也不给——禁用控件仍会暗示「以后能改」），
 * 只读它们是为了让用户在弹窗内看全账户上下文，而不是让用户以为将来能改。
 *
 * 币种在**新建时必选**：它是一切金额的计价单位，也是记账时的硬约束（跨币种无法交易）。
 * 候选只取启用的币种，初值为系统默认币种（由后端标出，前端不再另算一次回退）。
 *
 * 停用即软删除（数据行保留、可重新启用），故按钮文案统一用「停用」而非「删除」。
 */
import { computed, nextTick, onMounted, ref, watch } from 'vue'
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
import { useCurrenciesStore } from '@/stores/currencies'

const accountSets = useAccountSetsStore()
const accountsStore = useAccountsStore()
const currenciesStore = useCurrenciesStore()

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

/**
 * 当前页签的账户类型。
 *
 * 页签集合直接复用 `ACCOUNT_TYPE_OPTIONS`（资金/负债/往来三项）：它与后端枚举里
 * 「用户可手工指定的类型」本就是同一个集合，另立一份清单只会多一处需要同步的地方。
 * 默认停在资金账户——它是记账里最常用的一类，首屏不该是空的。
 */
const activeType = ref<AccountType>('Fund')

/** 页签条容器：键盘导航时据此在页签之间移动焦点。 */
const tablistRef = ref<HTMLElement | null>(null)

/** 新建弹窗是否可见。 */
const createOpen = ref(false)

/** 新建弹窗的面板元素：打开时聚焦，使 `Esc` 监听在弹窗内生效。 */
const createPanelRef = ref<HTMLElement | null>(null)

/** 新建表单。 */
const newName = ref('')
const newScope = ref<AccountScope>('Personal')
const newType = ref<AccountType>('Fund')
/** 期初金额：输入框是 `type="number"`，v-model 会自动把值转成 number（空串除外），故此处必须是联合类型。 */
const newInitialBalance = ref<string | number>('0')
/** 新建账户的币种代码；创建后不可修改。 */
const newCurrencyCode = ref('')

/** 自定义账户 Id 从 1 起自增，`0` 可安全用作「新建表单提交中」的哨兵值。 */
const NEW_ID = 0
/** 正在提交的账户 Id（`NEW_ID` 表示新建）；用于禁用按钮、避免重复提交。 */
const pendingId = ref<number | null>(null)

/**
 * 正在修改的账户；`null` 表示修改弹窗已关闭。
 *
 * 弹窗内的只读上下文（归属范围/类型/期初金额/余额）直接取自这个对象，是**打开时的快照**：
 * 唯一能改动账户的操作是保存成功，而成功即关窗，故快照不会有机会变成陈旧显示。
 */
const editTarget = ref<Account | null>(null)

/** 修改弹窗中可编辑的名称草稿；类型、归属范围、期初金额、余额均不可修改。 */
const editName = ref('')

/** 修改弹窗的面板元素：打开时聚焦，使 `Esc` 监听在弹窗内生效。 */
const editPanelRef = ref<HTMLElement | null>(null)

/** 待二次确认停用的账户 Id。 */
const confirmingId = ref<number | null>(null)

const accounts = computed(() => accountsStore.accounts)

/**
 * 当前页签下要呈现的账户。
 *
 * 分组依据是账户自带的 `type`，不额外维护一份「按类型分桶」的缓存——账套切换与增删改
 * 都会整体替换 `accountsStore.accounts`，派生值必须跟着源走才不会脱节。
 */
const tabAccounts = computed(() =>
  accounts.value.filter((account) => account.type === activeType.value),
)

/** 当前页签的类型标签，用于空态文案。 */
const activeTypeLabel = computed(() => ACCOUNT_TYPE_LABELS[activeType.value])

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
    // 币种字典与账户列表一并拉取：新建弹窗需要币种候选，列表需要币种代码的展示文本
    await Promise.all([accountsStore.list(showInactive.value), currenciesStore.loadActive()])
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载账户失败'
  }
}

/** 打开新建弹窗：每次都把表单复位，避免上一次填了一半的内容留在弹窗里。 */
async function openCreate(): Promise<void> {
  newName.value = ''
  newScope.value = 'Personal'
  // 类型默认取当前页签——「新增时自动调整账户类型」这个要求就落在这一行
  newType.value = activeType.value
  newInitialBalance.value = '0'
  // 币种初值取系统默认币种：绝大多数账户都用它，改选是少数情况
  newCurrencyCode.value = currenciesStore.defaultCode ?? currenciesStore.currencies[0]?.code ?? ''
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
 * 而去重试一个其实已经落表了的账户。
 */
function closeCreate(): void {
  if (pendingId.value !== null) {
    return
  }

  createOpen.value = false
}

/** 新建账户；成功后关闭弹窗。 */
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

  if (newCurrencyCode.value.length === 0) {
    errorMessage.value = '请选择币种'
    return
  }

  // 先记下归属范围的中文标签：下面的回调会把 newScope 复位前先用于提示文案
  const scopeLabel = ACCOUNT_SCOPE_LABELS[newScope.value]
  // 类型要在 await 之前取得：它既要生成提示文案，还要决定落表后停在哪个页签
  const createdType = newType.value
  const typeLabel = ACCOUNT_TYPE_LABELS[createdType]
  // 币种同样要在 await 之前取得，理由与类型相同：它要进提示文案
  const createdCurrency = newCurrencyCode.value

  const ok = await run(async () => {
    await accountsStore.create({
      name,
      scope: newScope.value,
      type: createdType,
      initialBalance,
      currencyCode: newCurrencyCode.value,
    })
    notice.value = `已新建${scopeLabel}${typeLabel} ${name}（${createdCurrency}）`
  }, NEW_ID)

  if (ok) {
    createOpen.value = false

    // 用户在弹窗里改选了别的类型：不切页签的话，新建的账户不在当前列表里，
    // 会让人以为「没建成功」而重复提交；切过去，让它在页签下当场可见。
    if (createdType !== activeType.value) {
      activeType.value = createdType
    }
  }
}

/** 打开修改弹窗：载入名称草稿并复位上一次的提示。 */
async function openEdit(account: Account): Promise<void> {
  editTarget.value = account
  editName.value = account.name
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

/** 保存修改。名称以外的字段都不可修改，故请求体只有名称。 */
async function saveEdit(): Promise<void> {
  const target = editTarget.value
  if (target === null) {
    return
  }

  const name = editName.value.trim()
  if (name.length === 0) {
    errorMessage.value = '账户名称不能为空'
    return
  }

  const ok = await run(async () => {
    await accountsStore.update(target.id, { name })
    notice.value = `已保存账户 ${name}`
  }, target.id)

  if (ok) {
    editTarget.value = null
  }
}

/**
 * 页签键盘导航：方向键在页签之间移动并即时切换，`Home` / `End` 跳首尾。
 *
 * 这是 ARIA tabs 的标准形态（roving tabindex：只有活动页签在 Tab 序列里），
 * 故切换后必须把焦点交给新页签——否则焦点留在原地，而用户看到的已是另一个页签的内容。
 */
function onTabKeydown(event: KeyboardEvent, index: number): void {
  const count = ACCOUNT_TYPE_OPTIONS.length
  let target = index

  if (event.key === 'ArrowRight') {
    target = (index + 1) % count
  } else if (event.key === 'ArrowLeft') {
    target = (index - 1 + count) % count
  } else if (event.key === 'Home') {
    target = 0
  } else if (event.key === 'End') {
    target = count - 1
  } else {
    return
  }

  // 越界在正常情况下不会发生（target 恒由取模或常量得出），此处的判空只为过类型收窄
  const nextType = ACCOUNT_TYPE_OPTIONS[target]
  if (nextType === undefined) {
    return
  }

  event.preventDefault()
  activeType.value = nextType.value

  void nextTick(() => {
    const tab = tablistRef.value?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[target]
    tab?.focus()
  })
}

/** 停用或启用账户（停用需先点一次再确认）。 */
async function toggleActive(account: Account): Promise<void> {
  const target = !account.isActive

  await run(async () => {
    await accountsStore.setActive(account.id, target)
    notice.value = target ? `已启用账户 ${account.name}` : `已停用账户 ${account.name}`
  }, account.id)
}

// 账套切换后必须重新拉取：账户是按账套隔离的，沿用旧列表会显示上一账套的数据。
// 未选择账套时清空列表，避免退出登录后仍残留可见数据。
watch(
  () => accountSets.currentId,
  async (currentId) => {
    confirmingId.value = null
    // 弹窗里填的是上一个账套的账户，换账套后不该继续沿用（两个弹窗都要关）
    createOpen.value = false
    editTarget.value = null
    notice.value = ''
    // 页签是视图偏好而非账套数据，换账套后保持不动：新账套里同样有该类型的账户

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
          :disabled="accountsStore.loading || !hasAccountSet"
          @click="load"
        >
          {{ accountsStore.loading ? '刷新中…' : '刷新' }}
        </button>
      </div>
    </header>

    <!-- 未选定账套：账户必须落在某个账套内，此时不渲染弹窗与表格 -->
    <p v-if="!hasAccountSet" class="hint">
      当前未选择账套，请先点击右上角的【切换】选择账套后再管理账户。
    </p>

    <template v-else>
      <!-- 弹窗打开时错误改在弹窗内呈现：页面级提示会被遮罩盖住，用户看不到 -->
      <p v-if="errorMessage && !createOpen && editTarget === null" class="error">
        {{ errorMessage }}
      </p>
      <p v-if="notice" class="notice">{{ notice }}</p>

      <div class="toolbar">
        <!-- 页签即账户类型：集合与顺序直接取自 ACCOUNT_TYPE_OPTIONS，不另立清单 -->
        <div ref="tablistRef" class="tabs" role="tablist" aria-label="账户类型">
          <button
            v-for="(option, index) in ACCOUNT_TYPE_OPTIONS"
            :id="`account-tab-${option.value}`"
            :key="option.value"
            type="button"
            class="tab"
            :class="{ 'tab-active': option.value === activeType }"
            role="tab"
            :aria-selected="option.value === activeType"
            aria-controls="account-tab-panel"
            :tabindex="option.value === activeType ? 0 : -1"
            @click="activeType = option.value"
            @keydown="onTabKeydown($event, index)"
          >
            {{ option.label }}
          </button>
        </div>

        <label class="toggle">
          <input v-model="showInactive" type="checkbox" :disabled="accountsStore.loading" />
          <span>显示已停用账户</span>
        </label>
      </div>

      <!-- 单个动态面板：切换页签换的只是它的内容，故 aria-labelledby 指向当前页签 -->
      <div id="account-tab-panel" role="tabpanel" :aria-labelledby="`account-tab-${activeType}`">
        <table class="table">
          <thead>
            <tr>
              <th>序号</th>
              <th>账户名称</th>
              <th>归属</th>
              <th>币种</th>
              <th class="amount">期初金额</th>
              <th class="amount">余额</th>
              <th>状态</th>
              <th>创建时间</th>
              <th class="actions-head">操作</th>
            </tr>
          </thead>
          <tbody>
            <!-- 序号按当前页签的列表重排：页签一变，行号就从 1 重新开始 -->
            <tr v-for="(account, index) in tabAccounts" :key="account.id">
              <td class="row-index">{{ index + 1 }}</td>
              <td class="name">{{ account.name }}</td>
              <td>
                <span class="badge" :class="account.scope === 'Public' ? 'badge-public' : ''">
                  {{ ACCOUNT_SCOPE_LABELS[account.scope] }}
                </span>
                <span v-if="account.ownerUsername" class="owner">{{ account.ownerUsername }}</span>
              </td>
              <!-- 币种同样一经创建不可修改：它是金额的计价单位，换币种会让既有余额变成另一个数 -->
              <td>
                <span class="badge badge-currency">{{ account.currencyCode }}</span>
              </td>
              <!-- 期初金额一经创建不可修改（已落成一笔期初交易），故只读呈现 -->
              <td class="amount">{{ formatAmount(account.initialBalance) }}</td>
              <td class="amount balance">{{ formatAmount(account.balance) }}</td>
              <td>
                <span class="badge" :class="account.isActive ? 'badge-active' : 'badge-inactive'">
                  {{ account.isActive ? '已启用' : '已停用' }}
                </span>
              </td>
              <td class="created">{{ formatDateTime(account.createdAt) }}</td>
              <td class="actions">
                <button
                  type="button"
                  class="ghost"
                  :disabled="pendingId !== null"
                  @click="openEdit(account)"
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
              </td>
            </tr>
            <tr v-if="!accountsStore.loading && tabAccounts.length === 0">
              <td class="empty" colspan="9">
                {{ showInactive ? `暂无${activeTypeLabel}` : `暂无启用的${activeTypeLabel}` }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>

    <!-- 新建弹窗：形态与账套选择弹窗一致（遮罩 + 面板 + 打开即聚焦，Esc / 点遮罩关闭） -->
    <div v-if="createOpen" class="mask" @click="closeCreate">
      <div
        ref="createPanelRef"
        class="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="account-create-title"
        tabindex="-1"
        @click.stop
        @keydown.esc="closeCreate"
      >
        <h2 id="account-create-title" class="dialog-title">新增账户</h2>
        <p class="dialog-subtitle">
          账户归属当前账套：个人账户的归属人固定为当前登录者，创建后不可转让；
          归属范围与所属账套一经创建不可修改。
        </p>

        <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

        <div class="dialog-form">
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
              <option
                v-for="option in ACCOUNT_SCOPE_OPTIONS"
                :key="option.value"
                :value="option.value"
              >
                {{ option.label }}
              </option>
            </select>
          </label>
          <label class="field">
            <span class="label">账户类型</span>
            <select v-model="newType" :disabled="pendingId !== null">
              <option
                v-for="option in ACCOUNT_TYPE_OPTIONS"
                :key="option.value"
                :value="option.value"
              >
                {{ option.label }}
              </option>
            </select>
          </label>
          <label class="field">
            <span class="label">币种</span>
            <select v-model="newCurrencyCode" :disabled="pendingId !== null">
              <option v-if="currenciesStore.currencies.length === 0" value="">暂无可用币种</option>
              <option
                v-for="currency in currenciesStore.currencies"
                :key="currency.id"
                :value="currency.code"
              >
                {{ currency.code }} {{ currency.name }}
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
        </div>

        <p class="dialog-hint">
          类型、币种与期初金额一经保存不可修改；期初金额非 0 时后端会同时落成一笔期初交易。
          只有币种相同的账户之间才能记账，故币种请按该账户实际使用的货币选择。
          账本账户由系统在期初入账时自动创建，不接受手工建立，也不在本列表呈现。
        </p>

        <div class="dialog-actions">
          <button
            type="button"
            class="submit"
            :disabled="pendingId !== null"
            @click="createAccount"
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
        aria-labelledby="account-edit-title"
        tabindex="-1"
        @click.stop
        @keydown.esc="closeEdit"
      >
        <h2 id="account-edit-title" class="dialog-title">修改账户</h2>
        <p class="dialog-subtitle">
          只有名称可以修改：账户类型、归属范围、币种与期初金额一经创建即不可变更。
        </p>

        <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

        <div class="dialog-form">
          <label class="field">
            <span class="label">名称</span>
            <input
              v-model="editName"
              type="text"
              maxlength="64"
              placeholder="必填，同一归属范围内不重名"
              :disabled="pendingId !== null"
            />
          </label>
        </div>

        <!-- 不可修改的字段一律只读文本呈现：不给控件，连禁用的也不给 -->
        <dl class="dialog-readonly">
          <div class="readonly-row">
            <dt>归属范围</dt>
            <dd>
              <span class="badge" :class="editTarget.scope === 'Public' ? 'badge-public' : ''">
                {{ ACCOUNT_SCOPE_LABELS[editTarget.scope] }}
              </span>
              <span v-if="editTarget.ownerUsername" class="owner">
                {{ editTarget.ownerUsername }}
              </span>
            </dd>
          </div>
          <div class="readonly-row">
            <dt>账户类型</dt>
            <dd>{{ ACCOUNT_TYPE_LABELS[editTarget.type] }}</dd>
          </div>
          <div class="readonly-row">
            <dt>币种</dt>
            <dd>{{ editTarget.currencyCode }}</dd>
          </div>
          <div class="readonly-row">
            <dt>期初金额</dt>
            <dd>{{ formatAmount(editTarget.initialBalance) }}</dd>
          </div>
          <div class="readonly-row">
            <dt>余额</dt>
            <dd class="balance">{{ formatAmount(editTarget.balance) }}</dd>
          </div>
        </dl>

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

/* 页头操作区：主操作「新增」在前，次要的「刷新」在后 */
.head-actions {
  display: flex;
  flex: none;
  align-items: center;
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

/* 页签与「显示已停用」同处一行：页签靠左随内容伸缩，开关靠右固定 */
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
}

/* 页签条：只在这里横向滚动，不撑破内容区 */
.tabs {
  display: flex;
  min-width: 0;
  gap: 0.35rem;
  overflow-x: auto;
  padding-bottom: 0.15rem;
}

.tab {
  padding: 0.4rem 0.9rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: none;
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  white-space: nowrap;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s,
    color 0.3s;
}

/* 活动页签复用主色：与 .badge-public / .notice 同一套语义色，不引入新色值 */
.tab-active {
  border-color: var(--color-accent);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
  font-weight: 600;
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

/* 列表行号：按当前列表顺序连续编号，与账户 Id 无关（停用账户后不会断号） */
.row-index {
  opacity: 0.6;
  white-space: nowrap;
}

.name {
  font-weight: 600;
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

/* 币种用等宽字呈现：CNY / USD 三个字母要竖着对齐才好扫读 */
.badge-currency {
  border-color: var(--color-border-hover);
  font-variant-numeric: tabular-nums;
  letter-spacing: 0.02em;
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
  /* 字段多、窄屏下弹窗可能高于视口：自身滚动，不撑破遮罩 */
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

/* 修改弹窗里的只读上下文：一列标签一列值，与表格的字段顺序无关，只求一眼看全 */
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

/* 实心主色按钮：页头「新增」与弹窗「保存」共用的强视觉锚点（与登录页提交按钮同源） */
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
