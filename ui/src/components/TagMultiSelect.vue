<script setup lang="ts">
/**
 * 标签多选框：已选标签以芯片（chip）呈现，可继续输入关键词从候选中添加，也可直接敲一个新名字。
 *
 * 与 `AccountSearchSelect.vue` / `CategorySearchSelect.vue` 同构（一个输入框、输入即筛选、
 * `@mousedown.prevent` 的候选列表），但**合并成一个通用组件是做不到的**：那两个是单值选择框，
 * 契约是「文本 + 一个主键」两个 v-model；本组件是**多值**的，契约是「一个标签数组」。
 * 若为一个单值、一个多值抽公共壳，得到的抽象要靠插槽与 props 把两种数据形状再传回去，
 * 比三份直白的实现更难读（与 `CategorySearchSelect` 里「不合并成通用组件」是同一取舍）。
 *
 * 与父组件之间是**一个 v-model**：`v-model:selected` 是已选标签数组，**不需要**文本那一路——
 * 标签草稿（输入框里还没提交的那几个字）不是载荷的一部分，敲下回车即变成芯片，故它只是
 * 组件内部的状态。这与分类框「文本本身就是一个载荷字段（`categoryName`）」刻意不同。
 *
 * **已选与候选是两份独立的数据**（`selected` 是 `TagRef[]`、`options` 是 `Tag[]`）：
 * 芯片一律按 `selected` 渲染，**绝不查候选表**——交易上挂着的可能是**已停用**的标签
 * （停用是「不再供新记账选择」，不是「历史上从未用过」），候选里没有它，若芯片去候选里找名字
 * 就会当场显示成空白，把一笔标过标签的账显示成没标。
 *
 * **允许落在候选之外**（没有 `freeText` 开关，因为标签**永远**接受按名创建）：
 * 敲一个候选里没有的名字，会以 {@link NEW_TAG_ID} 为主键进 `selected`，提交时交给后端自动创建。
 * 这与分类框同一口径，且没有账户那样的类型限制（转账也能按名建标签）。
 *
 * **选中即收起**：点【添加】或点选一个候选之后，收起候选列表并让输入框失焦——
 * 与 `AccountSearchSelect` / `CategorySearchSelect` 同一口径（那两个的 `choose()` 结尾都是
 * `open.value = false`，注释逐字写着「并收起列表」）。本组件此前是三者里唯一的例外：
 * `choose()` 与 `commitDraft()` 的结尾都是 `open.value = true`，点完列表仍挂着、遮住下方字段。
 * 收起的动作与理由收在 {@link collapseOptions}。
 *
 * **焦点口径的取舍（用户确认）**：收起点同时失焦，故**回车提交草稿后输入框也会失去焦点**，
 * 此时直接敲下一个标签名不会生效，需先点回输入框——点回即由 `@focus` 重新展开候选，
 * 因而「收起」不会把选择框锁死。**不要**改成「打字即重新展开」（给输入框加 `@input` 开列表）：
 * 那会把已确认的失焦口径悄悄回退成另一种行为。
 */
import { computed, ref } from 'vue'
import { NEW_TAG_ID, type Tag, type TagRef } from '@/stores/tags'

const props = defineProps<{
  /** 输入框元素的 id，供父组件的 `<label for>` 关联。 */
  inputId: string
  /** 已选标签（可能含已停用的，见文件头）。 */
  selected: TagRef[]
  /**
   * 候选标签（调用方按需过滤，通常是当前账套内**启用**的标签）。
   *
   * 已选中的项不在候选里重复出现（由 {@link filtered} 排除），故调用方不必先做这层减法。
   */
  options: Tag[]
  /** 输入框的占位提示。 */
  placeholder?: string
  /** 是否禁用。 */
  disabled?: boolean
}>()

const emit = defineEmits<{
  (event: 'update:selected', value: TagRef[]): void
}>()

/** 标签名最大长度，与后端 `Tag.Name` 的列长一致。 */
const TAG_NAME_MAX_LENGTH = 32

/**
 * 输入框草稿。
 *
 * **组件内部状态而非 v-model**：它从不作为载荷提交（回车/点「添加」即变成芯片），
 * 父组件复位表单时只需换掉 `selected`，不必也照看一份草稿——两份状态就要两处同步，
 * 漏掉一处即出现「芯片清空了、输入框里那几个字还在」。
 */
const draft = ref('')

/** 候选列表是否展开。 */
const open = ref(false)

/** 输入框元素：{@link collapseOptions} 需要它来主动失焦。 */
const inputRef = ref<HTMLInputElement | null>(null)

/** 草稿去空白后的比对键（标签名在账套内**不区分大小写**唯一，故比对也按小写）。 */
const draftKey = computed(() => draft.value.trim().toLowerCase())

/** 草稿是否已经是一个已选标签（按名字判断，理由同上）。 */
const draftAlreadySelected = computed(() =>
  props.selected.some((tag) => tag.name.trim().toLowerCase() === draftKey.value),
)

/**
 * 候选列表：先排除已选中的（按**名字**排除，不按主键）。
 *
 * 按名字而非主键：手工敲出来的标签以 `NEW_TAG_ID` 进数组，用它去比主键永远比不上，
 * 于是「刚敲过『报销』，再敲一次」仍会看到候选里的「报销」，点下去就得到两个同名芯片。
 * 名字才是账套内唯一的那个键，排除也照它来。
 *
 * 关键词为空时返回全部候选——刚聚焦时应当能看到完整列表，而不是一片空白。
 */
const filtered = computed<Tag[]>(() => {
  const keyword = draftKey.value
  const selectedNames = new Set(props.selected.map((tag) => tag.name.trim().toLowerCase()))

  return props.options.filter(
    (tag) =>
      !selectedNames.has(tag.name.trim().toLowerCase()) &&
      (keyword.length === 0 || tag.name.toLowerCase().includes(keyword)),
  )
})

/**
 * 关键词非空、候选为空、且它还不是已选标签：提示「提交后将自动创建」。
 *
 * 第三个条件不可省：草稿与某个已选标签同名时候选同样为空（被 {@link filtered} 排除了），
 * 此时提示「将自动创建」是错的——它不但不会新建，连添加都会因去重而无声跳过。
 */
const showNoMatch = computed(
  () => draftKey.value.length > 0 && !draftAlreadySelected.value && filtered.value.length === 0,
)

/**
 * 芯片的渲染键。
 *
 * 不能只用 `tag.id`：手工敲出来的那几个标签**共享** {@link NEW_TAG_ID}，
 * 用主键作键会让 Vue 认为它们是同一个节点（同一笔账上敲两个新名字就会丢一个）。
 * 名字在账套内唯一，故 `NEW_TAG_ID` 的项用名字作键。
 */
function chipKey(tag: TagRef): string {
  return tag.id === NEW_TAG_ID ? `new:${tag.name}` : `id:${tag.id}`
}

/**
 * 追加一个标签（已存在同名的不重复添加）。
 *
 * 去重按**名字**：后端对标签名的唯一性判据是「账套内不区分大小写唯一」，
 * 界面若按主键去重，就会出现两个芯片指向同一行（一个带主键、一个待创建），
 * 提交后被后端合成一条，而用户以为自己标了两个。
 */
function addTag(ref: TagRef): void {
  const key = ref.name.trim().toLowerCase()
  if (key.length === 0) {
    return
  }

  if (props.selected.some((tag) => tag.name.trim().toLowerCase() === key)) {
    return
  }

  emit('update:selected', [...props.selected, ref])
}

/**
 * 收起候选列表并让输入框失焦。
 *
 * **两件事都要做，缺一不可**：
 * - 只置 `open = false` 不够——{@link choose} 走的是候选按钮的 `@mousedown.prevent`，
 *   输入框**不会**自然失焦，而本组件开列表的时机只有 `@focus` 一个，于是会留下
 *   「列表收起了、输入框却仍聚焦，接着敲字看不到候选」这个中间态；
 * - 只依赖失焦也不够——点【添加】时输入框本就先失焦（`@blur` 已把 `open` 置假），
 *   但那之后 {@link commitDraft} 才跑，故收起点必须显式再置一次。
 *
 * 失焦是用户确认的口径：添加或选中即「这一轮选标签结束」，焦点离开标签编辑框；
 * 要接着添加下一个标签，点回输入框即可（`@focus` → `open = true`），故收起不会锁死。
 */
function collapseOptions(): void {
  open.value = false
  inputRef.value?.blur()
}

/**
 * 点选一个候选：直接落定它的主键与名称（这是我方已知存在的标签，不会被误判成新名字），
 * 随后收起候选列表并让输入框失焦。
 */
function choose(tag: Tag): void {
  addTag({ id: tag.id, name: tag.name })
  draft.value = ''
  collapseOptions()
}

/**
 * 提交草稿（回车或点【添加】）。
 *
 * 命中候选则沿用它的主键；否则以 {@link NEW_TAG_ID} 进数组，由后端按名创建。
 * 命中判定按名字全等（与 {@link filtered} 同一把钥匙）：用户敲出「报销」而候选里正有「报销」时，
 * 提交的应当是那个已有标签的主键，而不是一个待创建的同名项——否则后端要么报重名、
 * 要么把它归到既有标签上，界面却多绕了一圈。
 *
 * 与点选候选一样，收尾也收起候选列表并让输入框失焦：「添加」即这一轮选标签结束。
 */
function commitDraft(): void {
  const name = draft.value.trim()
  if (name.length === 0) {
    return
  }

  const hit = props.options.find((tag) => tag.name.toLowerCase() === name.toLowerCase())
  addTag(hit === undefined ? { id: NEW_TAG_ID, name } : { id: hit.id, name: hit.name })
  draft.value = ''
  collapseOptions()
}

/** 移除一个已选标签。 */
function removeTag(tag: TagRef): void {
  emit(
    'update:selected',
    props.selected.filter((item) => item !== tag),
  )
}
</script>

<template>
  <div class="tag-picker">
    <!-- 芯片区：已选标签各自带一个移除按钮。
         不用「输入框为空时按退格删掉最后一个」那套键盘约定——触屏上根本没有退格键，
         而每个芯片自带按钮是两种输入方式下都成立的唯一做法 -->
    <ul v-if="selected.length > 0" class="chips">
      <li v-for="tag in selected" :key="chipKey(tag)" class="chip">
        <span class="chip-name">{{ tag.name }}</span>
        <button
          type="button"
          class="chip-remove"
          :aria-label="`移除标签 ${tag.name}`"
          :disabled="disabled"
          @click="removeTag(tag)"
        >
          ×
        </button>
      </li>
    </ul>

    <div class="entry">
      <input
        ref="inputRef"
        :id="inputId"
        v-model="draft"
        type="text"
        autocomplete="off"
        :maxlength="TAG_NAME_MAX_LENGTH"
        :placeholder="placeholder"
        :disabled="disabled"
        @focus="open = true"
        @blur="open = false"
        @keydown.enter.prevent="commitDraft"
      />

      <!-- 触屏上没有「回车」这个键，故提交草稿另给一个按钮；键盘用户回车即可，两者等价 -->
      <button
        type="button"
        class="add"
        :disabled="disabled || draft.trim().length === 0"
        @click="commitDraft"
      >
        添加
      </button>
    </div>

    <!-- @mousedown.prevent：按下时不让输入框失焦，点击事件才会真正落到候选上 -->
    <ul v-if="open && filtered.length > 0" class="options">
      <li v-for="tag in filtered" :key="tag.id">
        <button type="button" class="option" @mousedown.prevent="choose(tag)">
          <span class="option-name">{{ tag.name }}</span>
        </button>
      </li>
    </ul>

    <p v-if="open && showNoMatch" class="tip" @mousedown.prevent>
      「{{ draft.trim() }}」不是已有标签，提交后将自动创建。
    </p>
  </div>
</template>

<style scoped>
/* 相对定位的容器：候选列表与提示浮在输入行下方，不撑开表单布局 */
.tag-picker {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 0.35rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: 0.2rem;
  padding: 0.15rem 0.3rem 0.15rem 0.55rem;
  border: 1px solid var(--color-accent);
  border-radius: 999px;
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
  font-size: 12.5px;
  line-height: 1.6;
}

/* 标签名可能很长（上限 32 位）：截断而不是把移除按钮挤出芯片 */
.chip-name {
  max-width: 12rem;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.chip-remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.25rem;
  height: 1.25rem;
  padding: 0;
  border: 0;
  border-radius: 999px;
  background: none;
  color: inherit;
  font-size: 14px;
  font-family: inherit;
  line-height: 1;
  cursor: pointer;
}

/* 输入行：输入框吃掉剩余宽度，【添加】按钮固定靠右 */
.entry {
  display: flex;
  align-items: center;
  gap: 0.4rem;
}

.entry input {
  flex: 1 1 auto;
  /* 没有它，flex 子项的最小内容宽度会顶穿父容器（候选列表的绝对定位宽度也跟着错） */
  min-width: 0;
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 13px;
  font-family: inherit;
  box-shadow: var(--shadow-control);
}

.entry input:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

.add {
  flex: none;
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: none;
  color: var(--color-accent-strong);
  font-size: 12.5px;
  font-family: inherit;
  cursor: pointer;
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
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
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

  .add:not(:disabled):hover {
    background-color: var(--color-accent-soft);
  }
}

.add:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

/*
 * 窄屏：芯片的移除按钮与【添加】按钮放大到触控友好尺寸，只在断点内生效，PC 端观感零改动。
 * 断点值与 `App.vue` 一致（全站唯一断点）。
 */
@media (max-width: 1023px) {
  .chip-remove {
    width: 1.75rem;
    height: 1.75rem;
    font-size: 16px;
  }

  .add {
    min-height: 2.25rem;
    padding: 0.5rem 0.9rem;
    font-size: 13px;
  }
}
</style>
