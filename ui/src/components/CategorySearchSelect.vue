<script setup lang="ts">
/**
 * 分类搜索选择框：可手工输入关键词实时筛选，并从候选中选定一个分类。
 *
 * 与 `AccountSearchSelect.vue` 同构（两个 v-model、输入即筛选、`@mousedown.prevent` 的候选列表），
 * 但**刻意不合并成一个通用组件**：账户那份的候选元信息是归属范围与归属人、提示语讲的是
 * 「自动创建为个人往来账户」，都是账户专有的语义；抽成通用组件就要靠插槽与 props 把
 * 这些语义再传回去，得到的抽象比两份直白的实现更难读。两者的**契约**（props / emits）一致，
 * 未来若确有第三处需要同一形态，再谈合并。
 *
 * 与父组件之间是**两个 v-model**：`v-model:text` 是输入框文本，`v-model:id` 是选定分类主键。
 * 分开传递是因为两者并非一回事：用户可以输入一个**不存在**的分类名，此时文本有值而 `id` 为 `null`
 * ——那个名字会被后端自动创建为新分类。这正是「手工输入自动创建」的表达方式。
 *
 * 因此本组件**不做「文本必须命中候选」的校验**，也不阻止 `freeText` 之外的名字：
 * 分类允许留空（不填即「未分类」），且任何名字都是合法的待建分类。
 *
 * 只在**用户输入**时反推 `id`（命中同名分类则取之，否则清空），而**不监听 `text` 的变化**：
 * 父组件复位表单时会同时写入文本与 id，若此处再按文本反推一次，同名分类之间就会选错。
 */
import { computed, ref } from 'vue'
import type { Category } from '@/stores/categories'

const props = defineProps<{
  /** 输入框元素的 id，供父组件的 `<label for>` 关联。 */
  inputId: string
  /** 已选定分类的主键；未选定或输入的是新名字时为 `null`。 */
  id: number | null
  /** 输入框中的文本。 */
  text: string
  /** 候选分类（调用方按需过滤，通常是当前账套内**启用**的分类）。 */
  options: Category[]
  /** 占位提示。 */
  placeholder?: string
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
const filtered = computed<Category[]>(() => {
  const keyword = props.text.trim().toLowerCase()
  if (keyword.length === 0) {
    return props.options
  }

  return props.options.filter((category) => category.name.toLowerCase().includes(keyword))
})

/** 关键词非空且候选为空：提示「提交后将自动创建」。 */
const showNoMatch = computed(() => props.text.trim().length > 0 && filtered.value.length === 0)

/**
 * 命中的同名分类：**去空白后全等**（不是子串匹配），取第一个。
 *
 * 命中的候选里可能是**已停用**的分类：候选列表由调用方决定是否含停用项，
 * 而「文本与 id 必须描述同一个分类」这一条不因启停而变。
 */
function matchByName(value: string): Category | null {
  const normalized = value.trim().toLowerCase()
  if (normalized.length === 0) {
    return null
  }

  return props.options.find((category) => category.name.toLowerCase() === normalized) ?? null
}

/**
 * 输入时同步文本并反推 `id`。
 *
 * 反推而非保留旧 `id`：用户把「餐饮」改成「交通」后，若 `id` 仍指向餐饮，
 * 提交的就会是另一个分类——文本与选中项必须始终描述同一个分类。
 */
function onInput(event: Event): void {
  const value = (event.target as HTMLInputElement).value
  emit('update:text', value)
  emit('update:id', matchByName(value)?.id ?? null)
}

/** 点选一个候选：文本与 id 同时落定，并收起列表。 */
function choose(category: Category): void {
  emit('update:id', category.id)
  emit('update:text', category.name)
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
      <li v-for="category in filtered" :key="category.id">
        <button type="button" class="option" @mousedown.prevent="choose(category)">
          <span class="option-name">{{ category.name }}</span>
        </button>
      </li>
    </ul>

    <p v-if="open && showNoMatch" class="tip" @mousedown.prevent>
      「{{ text.trim() }}」不是已有分类，提交后将自动创建。
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
