<script setup lang="ts">
/**
 * 摘要输入框：可手工输入，也可以从**历史摘要**里选一条填进来——选中之后**仍可继续编辑**。
 *
 * 与 `CategorySearchSelect.vue` / `AccountSearchSelect.vue` 同一形态（聚焦展开、输入即按关键词
 * 本地筛选、`@mousedown.prevent` 的候选浮层），但**只有 `v-model:text` 一个 v-model、没有 `id`**：
 * 摘要是**自由文本**，不是字典项——库里没有一张「摘要表」，也没有「选中了哪一条记录」这回事，
 * 选中候选只是把那段文本填进输入框。因此本组件**不做「按名匹配主键」**（分类那套两步契约在这里
 * 没有对应物），也**没有「不是已有 X，提交后将自动创建」的提示**（摘要不创建任何记录，
 * 任何文本都是合法摘要）。这一段与 `AccountSearchSelect` 的取舍同源：契约不同就各写一份，
 * 硬套同一个组件只会让两边都多出一堆与本字段无关的分支。
 *
 * **选中之后输入框不失焦、文本可继续改**（`@mousedown.prevent` 让点击不夺焦点，故点完仍停在输入框里）：
 * 复用一条历史摘要的常见用法是「用它打底再改」——「早餐」改成「早餐 加蛋」、
 * 「9月份工资」改成「10月份工资」。这与 `TagMultiSelect` 的「选完收起并**失焦**」（#63）
 * 刻意不同：标签是**集合**，选完还要接着加下一个，焦点留在输入框里只会让下一次点击落在同一个框上；
 * 摘要是**单值**，选完就地编辑才是主路径。
 *
 * **关键词为空时列出全部候选**：刚聚焦时应当能看到最近用过的写法，而不是一片空白——
 * 「上次那句话怎么写的」正是用户点进来的理由。筛选用**去空白后不区分大小写**的子串匹配，
 * 与其余候选框同一口径；**筛选只在本地做**（候选是后端按账套 + 类型聚合回来的整份去重结果，
 * 条数受 `SUMMARY_OPTION_LIMIT` 约束），不为关键词再打一次接口。
 */
import { computed, ref } from 'vue'
import { formatDate } from '@/components/entryFormat'
import type { SummaryOption } from '@/stores/entries'

const props = defineProps<{
  /** 输入框元素的 id，供父组件的 `<label for>` 关联。 */
  inputId: string
  /** 输入框中的文本。 */
  text: string
  /** 候选摘要（由调用方按当前账套与记账类型取回）。 */
  options: SummaryOption[]
  /** 占位提示。 */
  placeholder?: string
  /** 是否禁用。 */
  disabled?: boolean
}>()

const emit = defineEmits<{
  (event: 'update:text', value: string): void
}>()

/**
 * 摘要最大长度，与后端 `Transaction.Summary` 的列长一致。
 *
 * 前端拦一道只是为了少一次往返（后端不接受超长摘要）。它收在本组件里而不是由两个调用方各写一遍：
 * 两处各写一个 `maxlength="128"`，改列长时漏改一处，就会有一处界面允许敲进去一段提交必然被拒的文本。
 */
const SUMMARY_MAX_LENGTH = 128

/** 候选列表是否展开。 */
const open = ref(false)

/**
 * 按关键词筛选候选：去空白后**不区分大小写**的子串匹配；关键词为空时返回全部候选。
 *
 * 用「包含」而不是「前缀」：摘要通常是「午餐 公司楼下」这类带前缀的词组，
 * 用户记住的往往是中间那一段（「公司」），前缀匹配会让它筛不出来。
 * 命中项按后端给的次序呈现（最近使用倒序），**不按匹配位置重排**——候选的先后是
 * 「上次什么时候用的」，不是「跟关键词有多像」。
 */
const filtered = computed<SummaryOption[]>(() => {
  const keyword = props.text.trim().toLowerCase()
  if (keyword.length === 0) {
    return props.options
  }

  return props.options.filter((option) => option.summary.toLowerCase().includes(keyword))
})

/** 输入即上报文本：本组件没有「选中项」这一层状态，文本就是全部。 */
function onInput(event: Event): void {
  emit('update:text', (event.target as HTMLInputElement).value)
}

/**
 * 点选一条候选：把它的文本落进输入框（覆盖当前文本）、收起列表。
 *
 * 覆盖而不是追加：用户点候选的意思是「我要这一条」，追加会得到一段两条摘要拼起来的新文本。
 * 要接着改的话，光标停在文本末尾、输入框仍有焦点——这是**选中之后可再编辑**的实现方式。
 */
function choose(option: SummaryOption): void {
  emit('update:text', option.summary)
  open.value = false
}
</script>

<template>
  <div class="picker">
    <input
      :id="inputId"
      :value="text"
      type="text"
      :maxlength="SUMMARY_MAX_LENGTH"
      autocomplete="off"
      :placeholder="placeholder"
      :disabled="disabled"
      @input="onInput"
      @focus="open = true"
      @blur="open = false"
    />

    <!-- @mousedown.prevent：按下时不让输入框失焦，点击事件才会真正落到候选上；
         它同时使输入框在点选之后**保持聚焦**，用户可以立刻接着改（见文件头） -->
    <ul v-if="open && filtered.length > 0" class="options">
      <li v-for="option in filtered" :key="option.summary">
        <button type="button" class="option" @mousedown.prevent="choose(option)">
          <span class="option-summary">{{ option.summary }}</span>
          <!-- 用法与次数是选它的依据（「我上次是怎么写的、用过几次」），但都不如摘要本身重要，
               故弱化排在右侧；次数用「次」而不是「笔」——用户记的是「记过几笔」，不是明细行数 -->
          <span class="option-meta">
            最近 {{ formatDate(option.lastUsedAt) }} · {{ option.usageCount }} 次
          </span>
        </button>
      </li>
    </ul>
  </div>
</template>

<style scoped>
/* 相对定位的容器：候选列表浮在输入框下方，不撑开表单布局 */
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

/* 摘要占弹性空间、超长时截断：候选行的宽度由输入框决定，长摘要若换行会把下拉撑成一大片。
   被截断的那部分在选中后会完整落进输入框，故这里截断不丢信息 */
.option-summary {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 600;
}

/* 「最近 X 月 X 日 · N 次」：全站的「次要小字」档（12.5px / 0.7），且**不换行**
   ——它是这条候选的注释，换行会把它与摘要拆到两行，读起来像另一条候选 */
.option-meta {
  flex: none;
  font-size: 12.5px;
  opacity: 0.7;
  white-space: nowrap;
}

@media (hover: hover) {
  .option:hover {
    background-color: var(--color-accent-soft);
  }
}
</style>
