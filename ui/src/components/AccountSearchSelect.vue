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
      <li v-for="account in filtered" :key="account.id">
        <button type="button" class="option" @mousedown.prevent="choose(account)">
          <span class="option-name">{{ account.name }}</span>
          <span class="option-meta">
            {{ ACCOUNT_SCOPE_LABELS[account.scope] }}
            <template v-if="account.ownerUsername"> · {{ account.ownerUsername }}</template>
          </span>
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
  justify-content: space-between;
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

.option-name {
  font-weight: 600;
}

.option-meta {
  flex: none;
  font-size: 12px;
  opacity: 0.7;
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
