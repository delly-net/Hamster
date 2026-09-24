<script setup lang="ts">
/**
 * 账户搜索选择框：可手工输入关键词实时筛选，并从候选中选定一个账户。
 *
 * 替代原生的 `<select>`——账户可能很多，逐个下拉翻找效率太低；输入即筛选。
 *
 * 与父组件之间是**两个 v-model**：`v-model:text` 是输入框文本，`v-model:id` 是选定账户主键。
 * 分开传递是因为两者并非一回事：`freeText` 为真时，用户可以输入一个**不存在**的账户名，
 * 此时文本有值而 `id` 为 `null`——那个名字会被后端自动创建为个人往来账户。
 *
 * 因此本组件**不做「文本必须命中候选」的校验**：校验归属调用方（主账户不接受不存在的名字，
 * 由 `freeText: false` 配合父组件的提交前检查表达）。
 *
 * 只在**用户输入**时反推 `id`（命中同名账户则取之，否则清空），而**不监听 `text` 的变化**：
 * 父组件复位表单时会同时写入文本与 id，若此处再按文本反推一次，同名账户之间就会选错。
 */
import { computed, ref } from 'vue'
import { ACCOUNT_SCOPE_LABELS, type Account } from '@/stores/accounts'

const props = defineProps<{
  /** 输入框元素的 id，供父组件的 `<label for>` 关联。 */
  inputId: string
  /** 已选定账户的主键；未选定或不存在的名字为 `null`。 */
  id: number | null
  /** 输入框中的文本。 */
  text: string
  /** 候选账户（已由调用方按币种等条件过滤）。 */
  options: Account[]
  /** 占位提示。 */
  placeholder?: string
  /**
   * 是否允许「输入一个不存在于候选中的账户名」。
   *
   * 为 `false` 时只做筛选，用户必须从候选中点选（用于主账户：记到不存在的账户上没有意义）；
   * 为 `true` 时允许落在候选之外（用于来源/目标账户：不存在的名字会被后端自动创建为往来账户）。
   */
  freeText: boolean
  /** 是否禁用。 */
  disabled?: boolean
}>()

const emit = defineEmits<{
  (event: 'update:id', value: number | null): void
  (event: 'update:text', value: string): void
}>()

/** 候选列表是否展开。 */
const open = ref(false)

/**
 * 按关键词筛选候选：去空白后**不区分大小写**的子串匹配。
 *
 * 关键词为空时返回全部候选——刚聚焦时应当能看到完整列表，而不是一片空白。
 */
const filtered = computed<Account[]>(() => {
  const keyword = props.text.trim().toLowerCase()
  if (keyword.length === 0) {
    return props.options
  }

  return props.options.filter((account) => account.name.toLowerCase().includes(keyword))
})

/** 关键词非空且候选为空：`freeText` 下要提示「将新建」，否则要提示「无匹配」。 */
const showNoMatch = computed(() => props.text.trim().length > 0 && filtered.value.length === 0)

/** 金额呈现：固定两位小数（千分位由 `Intl` 自带），与后端 `decimal(...,2)` 及账户管理页对齐。 */
const amountFormatter = new Intl.NumberFormat('zh-CN', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/**
 * 候选行右侧的余额文本，形如 `1,234.56 CNY`；余额不是有限数时返回 `null`。
 *
 * **币种代码刻意不省**：候选已按表单所选币种过滤过，同币种下数字本身并无歧义，
 * 但带上代码与金额标签「金额（{币种}）」、账户管理页的币种徽标同一口径，
 * 转账页两个选择框并排时也不必让人回头确认表单顶部选了什么。
 *
 * **返回 `null` 而非某个占位标记**——这与账目明细页的加固口径（金额缺失必须显式标记、
 * 不得静默留空）看似相反，区别在于**「空白」在两处的语义不同**：那里三列的空白表示
 * 「这行没有收入」，是正常语义，字段缺失必须显式标记才不会被误读；这里余额只是附加信息，
 * 整段不呈现不会被读成「该账户余额为零」。但**绝不能渲染出 `NaN CNY`**，故仍拦这一道。
 *
 * @param account 候选账户。
 * @returns 余额文本；余额非有限数时返回 `null`。
 */
function balanceText(account: Account): string | null {
  return Number.isFinite(account.balance)
    ? `${amountFormatter.format(account.balance)} ${account.currencyCode}`
    : null
}

/**
 * 候选行：账户 + 预先算好的余额文本。
 *
 * 余额文本在这里一次算好，模板里就不必对同一个账户调用两次格式化函数；
 * 候选列表本就随输入变化重算，多这一层映射没有额外开销。`filtered` 原样保留——
 * {@link showNoMatch} 与 `matchByName` 仍按它判断。
 */
const rows = computed(() =>
  filtered.value.map((account) => ({ account, balance: balanceText(account) })),
)

/** 命中的同名账户：**去空白后全等**（不是子串匹配），取第一个。 */
function matchByName(value: string): Account | null {
  const normalized = value.trim().toLowerCase()
  if (normalized.length === 0) {
    return null
  }

  return props.options.find((account) => account.name.toLowerCase() === normalized) ?? null
}

/**
 * 输入时同步文本并反推 `id`。
 *
 * 反推而非保留旧 `id`：用户把「工资卡」改成「信用卡」后，若 `id` 仍指向工资卡，
 * 提交的就会是另一个账户——文本与选中项必须始终描述同一个账户。
 */
function onInput(event: Event): void {
  const value = (event.target as HTMLInputElement).value
  emit('update:text', value)
  emit('update:id', matchByName(value)?.id ?? null)
}

/** 点选一个候选：文本与 id 同时落定，并收起列表。 */
function choose(account: Account): void {
  emit('update:id', account.id)
  emit('update:text', account.name)
  open.value = false
}
</script>

<template>
  <div class="picker">
    <input
      :id="inputId"
      :value="text"
      type="text"
      autocomplete="off"
      :placeholder="placeholder"
      :disabled="disabled"
      @input="onInput"
      @focus="open = true"
      @blur="open = false"
    />

    <!-- @mousedown.prevent：按下时不让输入框失焦，点击事件才会真正落到候选上 -->
    <ul v-if="open && filtered.length > 0" class="options">
      <li v-for="row in rows" :key="row.account.id">
        <button type="button" class="option" @mousedown.prevent="choose(row.account)">
          <span class="option-name">{{ row.account.name }}</span>
          <span class="option-meta">{{ ACCOUNT_SCOPE_LABELS[row.account.scope] }}</span>
          <!-- 余额排在候选行的最右侧并等宽呈现：同一浮层里各项余额因此纵向对齐，
               一眼能比出哪个账户钱多（选转出账户/支出账户时正是要看这个）。
               余额非有限数时不渲染这一段，而不是留个占位标记，理由见 `balanceText` -->
          <span v-if="row.balance" class="option-balance">{{ row.balance }}</span>
        </button>
      </li>
    </ul>

    <p v-if="open && showNoMatch" class="tip" @mousedown.prevent>
      <template v-if="freeText"
        >「{{ text.trim() }}」不是已有账户，提交后将自动创建为个人往来账户。</template
      >
      <template v-else>没有匹配的账户，请从候选中选择。</template>
    </p>
  </div>
</template>

<style scoped>
/* 相对定位的容器：候选列表与提示浮在输入框下方，不撑开表单布局 */
.picker {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.picker input {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  box-shadow: var(--shadow-control);
}

.picker input:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

.options {
  position: absolute;
  top: calc(100% + 0.2rem);
  left: 0;
  right: 0;
  z-index: 20;
  max-height: 12rem;
  overflow-y: auto;
  margin: 0;
  padding: 0.2rem;
  list-style: none;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

.option {
  display: flex;
  align-items: baseline;
  /* 弹性空间全部让给账户名（见 .option-name）：归属范围与余额依次靠右，故不用 space-between，
     否则夹在中间的那段会被推到行中央 */
  gap: 0.75rem;
  width: 100%;
  padding: 0.4rem 0.55rem;
  border: 0;
  border-radius: var(--radius-control);
  background: none;
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  text-align: left;
  cursor: pointer;
}

/* 超长账户名用省略号截断，而不是把右侧的归属范围与余额挤出容器 */
.option-name {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  font-weight: 600;
}

/* 只呈现归属范围，**不呈现归属人用户名**（`account.ownerUsername`）：
   单用户下每行都是同一个名字、零区分度，多用户下候选又已按可见性过滤过
   （他人个人账户根本不在候选里），那个名字与「这笔钱记到哪」无关——
   识别归属人的需求在账户管理页满足（那里照常显示）。
   一行要塞下账户名 + 归属范围 + 余额三段，浮层宽度又被输入框锁死，
   省下的横向空间全部还给账户名 */
.option-meta {
  flex: none;
  font-size: 12px;
  opacity: 0.7;
}

/* 余额与归属同为中性 meta 信息（不引入收支语义色：负号已表达符号，
   而记账页的类型主题色只覆盖标题/提交按钮/成功提示条，不含账户选择浮层）。
   等宽数字让同一浮层内各行的数位纵向对齐，便于直接比大小 */
.option-balance {
  flex: none;
  font-size: 12px;
  opacity: 0.7;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.tip {
  font-size: 12.5px;
  line-height: 1.6;
  opacity: 0.7;
  padding: 0 0.1rem;
}

@media (hover: hover) {
  .option:hover {
    background-color: var(--color-accent-soft);
  }
}
</style>
