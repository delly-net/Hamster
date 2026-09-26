<script setup lang="ts">
/**
 * 改账弹窗：从「账目明细」页的某一行打开，改写**整笔交易**。
 *
 * **改的是一笔交易，不是「被点中的那一行」**：后端会把借贷两条明细一并改写
 * （方向不变、金额同步、两端账户同步），复式记账的配平因此在任何一次改账后仍然成立。
 * 只改当前这一行的做法会让两条明细当场不等额，「一笔交易」这个概念随之瓦解——
 * 那样的接口不存在，本弹窗也不提供那种入口。
 *
 * **账户余额不需要任何额外动作**：余额是明细的派生值（后端按账户汇总带符号金额），
 * 库里没有余额列。故「改完同步重算相关账户余额」这件事在这里表现为「什么都不用做」，
 * 而不是「调一个重算接口」——改了明细，旧账户与新账户的下一次查询就都是新值。
 *
 * **交易类型不可改**：它决定两条明细的借贷方向，且「收支互改」在语义上是两笔不同的账，
 * 故类型只作只读上下文呈现（后端为改账单列了一份不含 `type` 的请求体）。
 * **币种同理不在可改字段里**：交易表没有币种列，币种随主账户走。两个账户的候选因此都按
 * **当前币种**过滤——币种在本次改账中不变，也就不会出现「换了个币种的账户、对手方币种对不上」
 * 那种只能靠后端 400 才能让用户发现的死局。
 *
 * 挂载即打开（父级只在可编辑的行上、用 `v-if` 控制本组件），故字段初值直接在 setup 里
 * 从被点的行还原，不另写一套「打开时复位」的 watch。
 */
import { computed, nextTick, onMounted, ref } from 'vue'
import { ApiError } from '@/api/http'
import AccountSearchSelect from '@/components/AccountSearchSelect.vue'
import CategorySearchSelect from '@/components/CategorySearchSelect.vue'
import TagMultiSelect from '@/components/TagMultiSelect.vue'
import { MONEY_ACCOUNT_TYPES, useAccountsStore } from '@/stores/accounts'
import { useCategoriesStore } from '@/stores/categories'
import { isEntryEditable, type Entry } from '@/stores/entries'
import { splitTagRefs, useTagsStore, type TagRef } from '@/stores/tags'
import {
  transactionModeMeta,
  useTransactionsStore,
  type TransactionModeMeta,
} from '@/stores/transactions'

const props = defineProps<{
  /** 被点开的那一行明细；父级只在可编辑的行上挂载本组件，故恒有值。 */
  entry: Entry
}>()

const emit = defineEmits<{
  /** 用户取消（遮罩 / `Esc` / 取消按钮）。 */
  close: []
  /** 保存成功；参数是给页面提示用的摘要文案（该行已改，页面要重新取数）。 */
  saved: [message: string]
}>()

const accountsStore = useAccountsStore()
const categoriesStore = useCategoriesStore()
const tagsStore = useTagsStore()
const transactionsStore = useTransactionsStore()

/** `YYYY-MM-DDTHH:mm`，`<input type="datetime-local">` 的原生取值格式。 */
const DATE_TIME_PATTERN = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/

/** 分类名最大长度，与后端 `Category.Name` 的列长一致。前端拦一道只是为了少一次往返。 */
const CATEGORY_NAME_MAX_LENGTH = 32

/** 标签名最大长度，与后端 `Tag.Name` 的列长一致。 */
const TAG_NAME_MAX_LENGTH = 32

/**
 * 取不到账户角色名时的兜底文案。
 *
 * 正常路径不会走到：父级只在 `isEntryEditable` 为真的行上挂载本组件，而它排除了期初余额
 * ——期初没有「主账户」这一角色（它的方向由期初金额的符号决定）。留着它是为了让「不该出现的类型」
 * 在界面上仍是一句读得通的话，而不是一片空白（同 `transactionTypeLabel` 的兜底口径）。
 */
const FALLBACK_MODE_META: TransactionModeMeta = {
  label: '账目',
  primaryLabel: '主账户',
  counterpartyLabel: '对手方账户',
  summaryPlaceholder: '',
}

/** 本次改账的字段文案与账户角色（收入账户 / 支出账户 / 转出账户 …）。 */
const meta = computed(() => transactionModeMeta(props.entry.transactionType) ?? FALLBACK_MODE_META)

/** 中文类型名，用于标题与提示文案。 */
const modeLabel = computed(() => meta.value.label)

/** 是否在改一笔转账；转账的几处差异（对手方必填、候选收敛到钱账户）都由它分支。 */
const isTransfer = computed(() => props.entry.transactionType === 'Transfer')

/**
 * 该行是否支持修改。
 *
 * 判据与页面上的编辑入口**同源**（`isEntryEditable`），此处再拦一道只是为了让
 * 「不发必然失败的请求」这条口径不依赖调用方：真正说了算的仍是后端的准入判定。
 */
const editable = computed(() => isEntryEditable(props.entry))

const errorMessage = ref('')

/**
 * 两个端点：文本与主键分开持有（理由见 `AccountSearchSelect` 的说明）。
 *
 * 初值由 `initFields()` 从被点的行还原——**不能把两个 ref 直接初始化成 `entry.accountId`**
 * 之类：哪一侧是主账户取决于 `entry.isPrimary`。
 */
const primaryAccountId = ref<number | null>(null)
const primaryAccountText = ref('')
const counterpartyAccountId = ref<number | null>(null)
const counterpartyAccountText = ref('')

/** 分类：可留空（即「未分类」，后端接受），可点选候选，也可改成一个尚不存在的名字。 */
const categoryId = ref<number | null>(null)
const categoryText = ref('')

/**
 * 已选标签，初值即这笔交易当前挂着的那些。
 *
 * **改账下标签是「覆盖」而不是「追加」**（与备注同一口径）：提交的 `tagIds` + `tagNames`
 * 代表这笔交易改完之后**应有的全部标签**，全删空即清空标签。故初值必须把现有标签**全部**填进来，
 * 少填一个就等于在保存时把它删掉了。
 */
const selectedTags = ref<TagRef[]>([])

/** 金额草稿。**声明为 `string`**：输入框用 `type="text"`，v-model 不转型，恒为字符串。 */
const draftAmount = ref('')
const draftOccurredAt = ref('')
const draftSummary = ref('')
const draftRemark = ref('')

/** 面板元素：打开时聚焦，使 `Esc` 监听在弹窗内生效。 */
const panelRef = ref<HTMLElement | null>(null)

/**
 * 从被点的行还原两个端点与各字段初值。
 *
 * **一笔交易可能占两行**（转账的两端都会在明细页各占一行），故不能假定「被点的行就是主账户行」：
 * `isPrimary` 由后端判定（方向是否等于该类型的主账户方向）并随行下发，前端只消费——
 * 算错的后果是「改到了另一个账户上」，与显示无关，故不在这里重判。
 * 该行为主账户行时 `accountId` 即主账户，否则主账户是它的对手方（`counterpartyAccountId`）。
 *
 * 对手方档位为 `Ledger`（系统账本账户）时后端**不给主键也不给名字**，两个 ref 随之留空——
 * 留空正是「款项来自/去往账套之外」在记账侧的写法，提交后后端会落回该币种的系统账本账户，
 * 原样往返、不留任何猜测。该档位的行**必然**是主账户行：账本账户从不作为明细行出现。
 */
function initFields(): void {
  const entry = props.entry

  primaryAccountId.value = entry.isPrimary ? entry.accountId : entry.counterpartyAccountId
  primaryAccountText.value = entry.isPrimary ? entry.accountName : (entry.counterpartyName ?? '')

  counterpartyAccountId.value = entry.isPrimary ? entry.counterpartyAccountId : entry.accountId
  counterpartyAccountText.value = entry.isPrimary ? (entry.counterpartyName ?? '') : entry.accountName

  // 金额取**恒正的原始金额**而不是界面上那个带符号/带千分位的形式：后者是呈现格式，
  // 拿它回填会在重新解析时出岔子（`1,234.56` 不是合法数字输入）。
  // 两条明细金额恒相等，故取哪一行的都一样。
  draftAmount.value = String(entry.amount)

  // 时间按**本地时区**回填：后端下发的是带 `Z` 的 UTC 时刻，而 `<input type="datetime-local">`
  // 的原生取值是本地时间，直接截取字符串前 16 位会整体前移八小时。
  draftOccurredAt.value = toLocalInput(entry.occurredAt)

  draftSummary.value = entry.summary
  draftRemark.value = entry.remark ?? ''

  // 分类主键与名称同生同灭（后端保证），故「有名字必有主键」，不存在只有名字没有主键的中间态
  categoryId.value = entry.categoryId
  categoryText.value = entry.categoryName ?? ''

  // 标签原样带着走：它们**都已经落库**（后端下发的是关联行对应的真实主键），
  // 故此处不带 NEW_TAG_ID 的项，直接沿用 id 即可——不重新按名字去候选里找一遍
  // （重复一遍匹配只会引入「改名之后对不上」这种自找的分支）
  selectedTags.value = [...entry.tags]
}

/**
 * 本次改账的币种：**取自主账户**。
 *
 * 不取自被点的行：那一行可能是对手方行（转账的转入侧），而币种是「这笔账记在哪个币种的账户上」，
 * 只有主账户能定。主账户必在 `accountsStore` 里——它由「账目明细」页自己加载
 * （`GET /api/accounts?includeInactive=true`），而这一行正是该页查出来的。
 */
const currencyCode = computed(
  () =>
    accountsStore.accounts.find((account) => account.id === primaryAccountId.value)?.currencyCode ??
    '',
)

/** 币种未知时不做币种过滤：按未知的币种过滤会把候选清空，连「原样保存」都做不到。 */
function matchesCurrency(accountCurrencyCode: string): boolean {
  return currencyCode.value.length === 0 || accountCurrencyCode === currencyCode.value
}

/**
 * 主账户候选：**恒为钱账户**（资金/负债），币种一致。
 *
 * 与记账表单同口径：主账户是「这笔钱记到哪个账户」，与「谁欠谁」无关。
 * 候选**含已停用账户**（这里是页面的账户列表，它本就是带 `includeInactive=true` 取的）——
 * 与记账表单刻意不同：记账是「新记一笔」，停用账户不该再被选中；改账的默认意图恰恰是「保持原样」，
 * 而这一笔可能正挂在一个已停用的账户上，候选里没有它，用户改一下文本就再也选不回来。
 */
const primaryAccountOptions = computed(() =>
  accountsStore.accounts.filter(
    (account) =>
      MONEY_ACCOUNT_TYPES.includes(account.type) && matchesCurrency(account.currencyCode),
  ),
)

/**
 * 对手方候选：与主账户同一份币种过滤，类型上**只有转账才收敛到钱账户**。
 *
 * 与主账户的差异是刻意的，且与记账表单逐字一致：收入/支出的对手方**正是**往来账户的落点
 * （「支出 现金 → 老王」的对手方就是老王），把往来账户挡在这里等于砍掉这个用法；
 * 而转账的两端都是钱，往来账户不参与，后端也会 400。
 *
 * 转账下 `freeText` 为假，即转入账户必须从候选中选定：转账没有「账套之外」这一说，
 * 按名新建出来的是往来账户，而转账只允许资金与负债账户。
 */
const counterpartyOptions = computed(() =>
  accountsStore.accounts.filter(
    (account) =>
      matchesCurrency(account.currencyCode) &&
      (!isTransfer.value || MONEY_ACCOUNT_TYPES.includes(account.type)),
  ),
)

/**
 * 分类候选：**只含启用中的分类，已停用的一律不出现在候选里**（本笔当前那一枚也不例外）。
 *
 * 停用即软删除，其语义是「不再供新记账选择」——改账同属「重新选一次分类」，
 * 故候选里不该再给出已停用的项（记账表单与账户候选是同一口径：`categoriesStore.list(false)`）。
 *
 * **但「候选里没有」不等于「这一笔不能保持原样」**：本笔当前挂着的分类可能正是已停用的，
 * 此时输入框仍回填它原本的名称（见 `initFields`，那个名字取自交易自己的数据而非候选表），
 * 保存时后端按名匹配**含已停用分类**，会归到原来那一行上、**不会**新建一个同名的。
 * 候选与输入框因此可能对不上，`CategorySearchSelect` 还会按自己的逻辑提示
 * 「不是已有分类，提交后将自动创建」——那句话在这种情形下是不准确的，
 * 故由 {@link showInactiveCategoryHint} 在字段下方补一句如实说明。
 * **不改选择框那句文案**：它同时服务记账表单，为一个只在本弹窗成立的例外去改公共组件，
 * 会让另外两处调用点也读到一句与它们无关的说明。
 *
 * 取数**仍取全量**（`loadDictionaries` 用 `list(true)`）：判「本笔那一枚是不是已停用的」
 * 必须有全量在手，把取数一并收窄成 `list(false)` 就判不出来了。
 *
 * 不做其他过滤：分类没有可见性维度，也不随币种变化——停用状态是这里唯一的收窄条件。
 */
const categoryOptions = computed(() =>
  categoriesStore.categories.filter((category) => category.isActive),
)

/**
 * 是否要说明「本笔当前的分类已停用」。
 *
 * 成立条件是「输入框里有名字、且这个名字对应一枚已停用的分类」：此时该名字既不在候选里
 * （故点开候选看不到它），保存后却又会**原样归到它上面**——不说明的话，
 * 用户会以为自己正在把这个分类改成一个新分类。
 *
 * 按**名称**匹配而不是按主键：真正的判据是「输入框里的这串字会不会被后端归到某个已停用分类上」，
 * 而用户完全可以在此处把名字改掉（改成一个启用分类的名字、或一个全新的名字），
 * 那时这句话就不成立了。按名匹配与后端 `FindByNameAsync` 的口径一致（不区分大小写、比 Trim 后的值）。
 */
const showInactiveCategoryHint = computed(() => {
  const name = categoryText.value.trim().toLowerCase()
  if (name.length === 0) {
    return false
  }

  return categoriesStore.categories.some(
    (category) => !category.isActive && category.name.trim().toLowerCase() === name,
  )
})

/**
 * 标签候选：**含已停用标签**，理由与 {@link categoryOptions} 逐字相同。
 *
 * 还有一条标签专有的理由：芯片按交易自己的 `tags` 渲染、不查候选表，故停用标签**已经**能正确显示；
 * 但用户把它删掉之后若想再加回来，候选里没有它就只能重新敲一遍名字（敲名字也能归到那个标签上，
 * 后端按名匹配含已停用项），多这一步纯属自找。此处一并列出。
 *
 * 不做其他过滤：标签没有可见性维度，也不随币种变化。
 */
const tagOptions = computed(() => tagsStore.tags)

/** 主账户字段的提示文案。 */
const primaryPlaceholder = computed(() =>
  primaryAccountOptions.value.length === 0
    ? '当前币种下没有可用账户'
    : '输入关键词筛选，从候选中选择',
)

/** 对手方字段的提示文案；转账下不再是「可留空」。 */
const counterpartyPlaceholder = computed(() =>
  isTransfer.value
    ? counterpartyOptions.value.length === 0
      ? '当前币种下没有可用账户'
      : '输入关键词筛选，从候选中选择'
    : '留空即账本账户（账套之外）',
)

/** 分类字段的提示文案。 */
const categoryPlaceholder = computed(() =>
  categoryOptions.value.length === 0 ? '暂无分类，可直接输入新分类名' : '可留空，或输入新分类名',
)

/** 标签字段的提示文案。 */
const tagPlaceholder = computed(() =>
  tagOptions.value.length === 0 ? '暂无标签，可直接输入新标签名' : '可留空，或输入新标签名',
)

/**
 * 是否要说明「当前对手方是系统账本账户」。
 *
 * 账本账户不在任何候选里，故它在界面上就是**一个空输入框**——而空输入框本身也能读成
 * 「这笔账没有对手方」。这一行说明把那个歧义消掉。用户一旦从候选里选定了账户就不再显示
 * （那时字段已有值，说的就不是同一件事了）。
 */
const showLedgerHint = computed(
  () =>
    !isTransfer.value &&
    props.entry.counterpartyKind === 'Ledger' &&
    counterpartyAccountId.value === null,
)

/** 补零到两位。 */
function pad(value: number): string {
  return String(value).padStart(2, '0')
}

/**
 * 把后端的 UTC 时刻折成 `datetime-local` 的原生取值（**本地时间**）。
 *
 * 与 {@link toUtcIso} 互为逆运算：两个函数必须成对使用，各自单独看都对不上号。
 */
function toLocalInput(value: string): string {
  const at = new Date(value)
  if (Number.isNaN(at.getTime())) {
    return ''
  }

  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}T${pad(at.getHours())}:${pad(at.getMinutes())}`
}

/**
 * 把 `datetime-local` 的本地时间文本转成 UTC 的 ISO 8601 时刻。
 *
 * 按 `YYYY-MM-DDTHH:mm` 拆段后用 `new Date(y, m - 1, d, h, min)` 构造——**这是本地时间**，
 * 再由 `toISOString()` 折成 UTC。直接 `new Date('2026-09-23T10:00')` 会按 UTC 解释该字符串，
 * 东八区下整体错位八小时。
 *
 * 精度到分钟为止（与记账表单同一口径、也是本页 `formatDateTime` 的呈现精度）：库里若存着秒，
 * 保存后秒会被归零——这与「用记账表单记一笔」的落库精度一致，不另造一套更高精度的输入方式。
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
 * 解析金额输入；用 `Number()` + `Number.isFinite` 而非 `parseFloat`
 * （后者对 `12abc` 会悄悄返回 `12`）。
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

/**
 * 拉取分类与标签的**全量**字典（`list(true)`，含已停用项）。
 *
 * 「取全量」与「候选列什么」是两件事，两张字典的理由并不相同：
 * - 分类：**候选只要启用的**（见 `categoryOptions`），但取数必须全量——
 *   判「本笔当前那一枚是不是已停用的」（`showInactiveCategoryHint`）要有全量在手，
 *   取数一并收窄成 `list(false)` 就再也判不出来。
 * - 标签：候选本身就含已停用（见 `tagOptions`），故取数同样必须全量。
 *
 * 账户候选不在这里拉：「账目明细」页已经取好了同一份（且口径一致、含已停用账户），
 * 本弹窗从页面上打开，那份列表必然已经在了——再拉一次只会多一次往返与一份可能不同步的数据。
 * 分类与标签在这两处不再共用：明细页的分类/标签筛选候选取的也是**全量**，
 * 与这里的取数口径一致但用途不同（那边是筛选条件、这里是本笔的取值），故仍在此各拉一次。
 */
async function loadDictionaries(): Promise<boolean> {
  try {
    await Promise.all([categoriesStore.list(true), tagsStore.list(true)])
    return true
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '加载分类与标签失败'
    return false
  }
}

/**
 * 保存修改。
 *
 * 前端先拦下明显不完整的输入、**不发请求**（后端错误已由 `http.ts` 拼成中文，
 * 但让用户为一处空字段等一次往返没有意义）；校验顺序与记账表单一致。
 */
async function submit(): Promise<void> {
  errorMessage.value = ''

  if (!editable.value) {
    errorMessage.value = '这一行不支持修改'
    return
  }

  const accountId = primaryAccountId.value
  if (accountId === null) {
    errorMessage.value = `请选择${meta.value.primaryLabel}`
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

  // 分类可留空（即「未分类」，后端接受）；填了名字就先在本机拦住超长，
  // 与后端 `Category.Name` 同一列长——让用户立刻看到问题，而不必为一处超长等一次往返
  const categoryNameDraft = categoryText.value.trim()
  if (categoryNameDraft.length > CATEGORY_NAME_MAX_LENGTH) {
    errorMessage.value = `分类名不能超过 ${CATEGORY_NAME_MAX_LENGTH} 位`
    return
  }

  // 标签：切成接口要的两份载荷（已有主键的 + 待按名创建的），与记账表单同一份判据与函数。
  // 超长同样先在本机拦下——选择框的输入框已带 maxlength，但载荷的合法性不该依赖子组件的一个属性
  const { tagIds, tagNames } = splitTagRefs(selectedTags.value)
  if (tagNames.some((name) => name.length > TAG_NAME_MAX_LENGTH)) {
    errorMessage.value = `标签名不能超过 ${TAG_NAME_MAX_LENGTH} 位`
    return
  }

  // 转账的两个端点都是真实账户，没有「账套之外」这一说，故转入账户必须选定（后端同样会拒）
  if (isTransfer.value && counterpartyAccountId.value === null) {
    errorMessage.value = `请选择${meta.value.counterpartyLabel}`
    return
  }

  // 收支的对手方可留空；与主账户重合时当场拦下——同一账户自转自的账是两条相互抵消的空交易
  if (counterpartyAccountId.value !== null && counterpartyAccountId.value === accountId) {
    errorMessage.value = `${meta.value.counterpartyLabel}不能与${meta.value.primaryLabel}是同一个账户`
    return
  }

  // 两者至多传一个：从候选中选定后用户又改了名字，此时子组件已按新文本清空了 id，
  // 故此处按「有 id 传 id、否则传名字」取值即可，不会同时送出两个互相矛盾的字段
  const counterpartyId = counterpartyAccountId.value
  const counterpartyName = counterpartyAccountText.value.trim()

  try {
    const updated = await transactionsStore.update(props.entry.transactionId, {
      accountId,
      amount,
      occurredAt,
      summary,
      remark: remark.length === 0 ? null : remark,
      counterpartyAccountId: counterpartyId,
      // 转账恒不传名字（后端拒绝非空取值）：转账的两端都是真实账户，按名新建出来的是往来账户，
      // 而转账只允许资金与负债账户。这一层防的是「转入账户留空」被将来放宽时悄悄开始按名创建
      counterpartyName:
        !isTransfer.value && counterpartyId === null && counterpartyName.length > 0
          ? counterpartyName
          : null,
      // 分类同为「有 id 传 id、否则传名字」；两者皆空即「未分类」，这是合法的，后端不报错
      categoryId: categoryId.value,
      categoryName:
        categoryId.value === null && categoryNameDraft.length > 0 ? categoryNameDraft : null,
      // 标签是**覆盖**而非追加：这两份载荷就是「改完之后应有的全部标签」，全删空即清空标签。
      // 两份都上报、由后端合并去重（多值与分类的二选一刻意不同，理由见 store）
      tagIds,
      tagNames,
    })

    // 提示读法沿用记账表单：转账读作「转出 A → 转入 B」，收支读作「主账户（对手方）」；
    // 对手方为账本账户时后端不回传名字，此处补成「账本账户」而不是留一个空括号。
    // 分类回显的是**后端落定的那个分类**（本次手工输入的名字可能是刚被自动创建的），
    // 而不是输入框里的文本：只有后端才知道这个名字最终归到了哪一条记录上。未分类时不显示这一段
    const categorySuffix = updated.categoryName === null ? '' : ` · 分类：${updated.categoryName}`
    // 标签同记账表单：回显**后端落定的那些**（本次手打的、被合并到既有标签上的都在其中），
    // 次序即提交次序；一个都没有时整段不显示——这正是「标签被清空了」的呈现
    const tagSuffix =
      updated.tags.length === 0 ? '' : ` · 标签：${updated.tags.map((tag) => tag.name).join('、')}`
    const metaSuffix = `${categorySuffix}${tagSuffix}`

    emit(
      'saved',
      isTransfer.value
        ? `已修改一笔转账：${updated.summary} 转出 ${updated.accountName} → 转入 ${updated.counterpartyName ?? '—'}${metaSuffix}`
        : `已修改一笔${modeLabel.value}：${updated.summary} ${updated.accountName}` +
            `（${meta.value.counterpartyLabel}：${updated.counterpartyName ?? '账本账户'}）${metaSuffix}`,
    )
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '保存失败'
  }
}

/**
 * 取消（遮罩 / `Esc` / 取消按钮）。
 *
 * 保存中不关：请求已经发出，此刻关掉弹窗只会让用户看不到它的结果。
 */
function requestClose(): void {
  if (transactionsStore.loading) {
    return
  }

  emit('close')
}

/** `Esc` 关闭。 */
function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    requestClose()
  }
}

initFields()

onMounted(async () => {
  // 初值已在 setup 里落好，挂载后先让弹窗按初值渲染出来，再补齐会变化的部分（候选列表）
  await nextTick()

  // **先聚焦，再拉候选**：聚焦是本弹窗的打开语义——Esc 关闭挂在**面板**的 keydown 上，
  // 焦点不在面板里键盘就整段失效（取消按钮仍可用，故只是部分能力失效，更难被发现）。
  // 把它挂在一次网络请求之后，就等于让「能不能按 Esc」取决于那次请求的快慢与成败。
  panelRef.value?.focus()

  await loadDictionaries()
})
</script>

<template>
  <!-- 遮罩层级与账套选择弹窗一致（z-index 30）：弹窗必须盖住窄屏抽屉与其遮罩 -->
  <div class="mask" @click="requestClose">
    <div
      ref="panelRef"
      class="panel"
      role="dialog"
      aria-modal="true"
      aria-labelledby="entry-edit-title"
      tabindex="-1"
      @click.stop
      @keydown="handleKeydown"
    >
      <h2 id="entry-edit-title" class="title">修改{{ modeLabel }}</h2>
      <p class="subtitle">
        修改的是<strong>整笔交易</strong>：借贷两条明细会一并改写（方向不变），
        相关账户的余额随之重算。交易类型与币种不可修改。
      </p>

      <!-- 只读上下文：类型与币种都不在可改字段里，但改账时必须看得见 -->
      <p class="context">
        <span>类型：<strong>{{ modeLabel }}</strong></span>
        <span>币种：<strong>{{ currencyCode || '—' }}</strong></span>
      </p>

      <!-- 表单自身不滚动，只有中间的字段区滚动：保存/取消始终留在视野里 -->
      <form class="form" @submit.prevent="submit">
        <div class="fields">
          <div class="field">
            <label class="label" for="edit-primary">{{ meta.primaryLabel }}</label>
            <AccountSearchSelect
              v-model:id="primaryAccountId"
              v-model:text="primaryAccountText"
              input-id="edit-primary"
              :options="primaryAccountOptions"
              :placeholder="primaryPlaceholder"
              :free-text="false"
            />
          </div>

          <div class="field">
            <label class="label" for="edit-counterparty">{{ meta.counterpartyLabel }}</label>
            <AccountSearchSelect
              v-model:id="counterpartyAccountId"
              v-model:text="counterpartyAccountText"
              input-id="edit-counterparty"
              :options="counterpartyOptions"
              :placeholder="counterpartyPlaceholder"
              :free-text="!isTransfer"
            />
            <p v-if="showLedgerHint" class="field-hint">
              当前对手方是系统账本账户（账套之外），保持留空即可。
            </p>
          </div>

          <div class="field">
            <label class="label" for="edit-category">分类（可选）</label>
            <CategorySearchSelect
              v-model:id="categoryId"
              v-model:text="categoryText"
              input-id="edit-category"
              :options="categoryOptions"
              :placeholder="categoryPlaceholder"
            />
            <!-- 候选里没有已停用分类，而本笔当前那一枚可能正是已停用的：
                 此时选择框会提示「提交后将自动创建」，与后端按名归到原分类上的事实不符，
                 故在字段下方如实说明（与账本账户那句 field-hint 同一做法） -->
            <p v-if="showInactiveCategoryHint" class="field-hint">
              「{{ categoryText.trim() }}」是已停用的分类，不在候选里；保持这个名字保存会<strong>原样归到它上面</strong>（不会新建同名的），
              要换成别的分类请从候选中重选。
            </p>
          </div>

          <div class="field">
            <label class="label" for="edit-tags">标签（可选，可多个）</label>
            <!-- 初值即这笔交易当前挂着的全部标签，删芯片即「这笔账不再标它」——改账下标签是覆盖语义 -->
            <TagMultiSelect
              v-model:selected="selectedTags"
              input-id="edit-tags"
              :options="tagOptions"
              :placeholder="tagPlaceholder"
            />
          </div>

          <div class="field">
            <label class="label" for="edit-amount">金额（{{ currencyCode || '—' }}）</label>
            <!-- 刻意用 type="text" 而非 type="number"：后者的 v-model 会隐式转型，
                 输入有效数字时是 number、清空时是 string，两种类型混在一个 ref 里 -->
            <input
              id="edit-amount"
              v-model="draftAmount"
              type="text"
              inputmode="decimal"
              autocomplete="off"
              placeholder="0.00"
            />
          </div>

          <div class="field">
            <label class="label" for="edit-occurred-at">发生时间</label>
            <input id="edit-occurred-at" v-model="draftOccurredAt" type="datetime-local" />
          </div>

          <div class="field">
            <label class="label" for="edit-summary">摘要</label>
            <input
              id="edit-summary"
              v-model="draftSummary"
              type="text"
              maxlength="128"
              autocomplete="off"
              :placeholder="meta.summaryPlaceholder"
            />
          </div>

          <div class="field">
            <label class="label" for="edit-remark">备注（可选）</label>
            <input
              id="edit-remark"
              v-model="draftRemark"
              type="text"
              maxlength="256"
              autocomplete="off"
            />
          </div>
        </div>

        <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

        <div class="actions">
          <button type="submit" class="submit" :disabled="transactionsStore.loading">
            {{ transactionsStore.loading ? '保存中…' : '保存修改' }}
          </button>
          <button
            type="button"
            class="ghost"
            :disabled="transactionsStore.loading"
            @click="requestClose"
          >
            取消
          </button>
        </div>
      </form>
    </div>
  </div>
</template>

<style scoped>
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

/* 比账套选择弹窗（26rem）宽：那里的内容是一个账户列表，一个窄列正合适；
   而本弹窗要装 8 个字段，窄列下只能竖排、字段区必然要滚动几屏。
   44rem = 704px，是本页`.fields`放下**两列 20rem**所需的最小宽度
   （2 × 320px + 0.7rem 列间距 ≈ 651px，加两侧 1.35rem 内边距 ≈ 694px，留 10px 余量），
   再宽也不会多出第三列（三列需要 971px，超过多数笔记本的可视区），故到此为止。
   **全站弹窗不再只有一种尺寸**：这个取舍是刻意的——弹窗宽度跟着它的内容走，
   而不是反过来让 8 个字段挤进 26rem 里去。 */
.panel {
  width: 100%;
  max-width: 44rem;
  max-height: 80vh;
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  padding: 1.25rem 1.35rem 1.15rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

.title {
  font-size: 18px;
  font-weight: 600;
  color: var(--color-heading);
}

.subtitle {
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.75;
}

/* 只读上下文：类型与币种不是可改字段，但要一眼看得见 */
.context {
  display: flex;
  flex-wrap: wrap;
  gap: 0.2rem 0.9rem;
  font-size: 12.5px;
  opacity: 0.85;
}

.form {
  /* min-height: 0 是让下面的 .fields 真正成为可滚动的溢出容器（flex 子项默认不收缩） */
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 0.7rem;
}

/* 两列自适应：窄屏自动并为一列，无需媒体查询（同记账表单 `EntryRecordForm` 的做法）。
   列宽下限 20rem（320px）的算式见那里：账户选择框的候选行要装得下
   「8 个汉字 + 归属范围 + 7 位数余额」≈ 308px，下限取 320px 留 12px 余量；
   用 min(20rem, 100%) 而非直接写 20rem，是为了容器本身窄于 320px 时不撑破面板造成横向滚动。
   `align-content: start` 不可省：字段区是定高滚动容器，默认的 stretch 会在字段少时
   把几行拉高摊开，同一页出现两种行距。 */
.fields {
  min-height: 0;
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(20rem, 100%), 1fr));
  align-content: start;
  gap: 0.7rem;
  overflow-y: auto;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  min-width: 0;
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

/* 金额等宽呈现，避免输入时数字左右跳动 */
#edit-amount {
  font-variant-numeric: tabular-nums;
}

.field-hint {
  font-size: 12px;
  line-height: 1.6;
  opacity: 0.7;
}

.error {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-danger-border);
  border-radius: var(--radius-control);
  background: var(--color-danger-soft);
  color: var(--color-danger);
  font-size: 13px;
  line-height: 1.7;
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}

/* 实心主色按钮：弹窗内的唯一强视觉锚点（与账套选择弹窗同源）。
   刻意**不随记账类型着色**（记账表单那样做是为了让「刚记了一笔支出」的提示与收入区分开）：
   改账只有一个动作，而这个按钮若在支出下变红，会被读成「危险操作」。 */
.submit {
  padding: 0.5rem 1.1rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: var(--color-accent);
  color: var(--color-accent-contrast);
  font-size: 13.5px;
  font-weight: 600;
  font-family: inherit;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s;
}

.ghost {
  padding: 0.5rem 1rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: none;
  color: inherit;
  font-size: 13.5px;
  font-family: inherit;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s,
    color 0.3s;
}

@media (hover: hover) {
  .submit:not(:disabled):hover {
    border-color: var(--color-accent-strong);
    background: var(--color-accent-strong);
  }

  .ghost:not(:disabled):hover {
    border-color: var(--color-accent);
    background-color: var(--color-accent-soft);
    color: var(--color-accent-strong);
  }
}

.submit:disabled,
.ghost:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
