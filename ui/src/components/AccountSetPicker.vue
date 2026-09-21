<script setup lang="ts">
/**
 * 账套选择弹窗：登录后的首次选择与 header 的【切换】共用同一组件。
 *
 * `required` 为真（可访问账套多于一个且尚未选定）时，遮罩点击与 `Esc` 均不关闭弹窗——
 * 但仍保留「退出登录」入口，避免把用户困在一个关不掉的弹窗里。
 */
import { nextTick, ref, watch } from 'vue'
import type { AccountSet } from '@/stores/accountSets'

const props = withDefaults(
  defineProps<{
    /** 是否可见。 */
    open: boolean
    /** 可选的账套。 */
    accounts: AccountSet[]
    /** 当前已选定的账套 Id；用于标记与默认选中。 */
    currentId: number | null
    /** 是否必须先做出选择（此时不允许取消）。 */
    required?: boolean
  }>(),
  { required: false },
)

const emit = defineEmits<{
  /** 用户确认了某个账套。 */
  select: [id: number]
  /** 用户取消（仅在非必选时触发）。 */
  close: []
  /** 必选场景下的逃生入口。 */
  logout: []
}>()

/** 弹窗内的待选账套；未选择前「确定」保持禁用。 */
const pendingId = ref<number | null>(null)

/** 面板元素：打开时聚焦，使 `Esc` 监听在弹窗内生效。 */
const panelRef = ref<HTMLElement | null>(null)

// 每次打开都以当前账套作为默认选中项，让「切换」面板回显现状
watch(
  () => props.open,
  async (open) => {
    if (!open) {
      return
    }

    pendingId.value = props.currentId
    await nextTick()
    panelRef.value?.focus()
  },
  { immediate: true },
)

/** 确认选择。 */
function confirm(): void {
  if (pendingId.value === null) {
    return
  }

  emit('select', pendingId.value)
}

/** 点击遮罩：仅非必选场景可关闭。 */
function handleMaskClick(): void {
  if (!props.required) {
    emit('close')
  }
}

/** `Esc` 关闭；必选场景忽略。 */
function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    handleMaskClick()
  }
}
</script>

<template>
  <div v-if="open" class="mask" @click="handleMaskClick">
    <div
      ref="panelRef"
      class="panel"
      role="dialog"
      aria-modal="true"
      aria-labelledby="account-set-picker-title"
      tabindex="-1"
      @click.stop
      @keydown="handleKeydown"
    >
      <h2 id="account-set-picker-title" class="title">
        {{ props.required ? '选择账套' : '切换账套' }}
      </h2>
      <p class="subtitle">
        {{
          props.required
            ? '你被关联了多个账套，请先选择一个账套再继续使用。'
            : '选择一个账套作为当前工作账套。'
        }}
      </p>

      <ul class="options">
        <li v-for="account in props.accounts" :key="account.id">
          <button
            type="button"
            class="option"
            :class="{ active: account.id === pendingId }"
            :aria-pressed="account.id === pendingId"
            @click="pendingId = account.id"
          >
            <span class="option-main">
              <span class="option-name">{{ account.name }}</span>
              <span v-if="account.remark" class="option-remark">{{ account.remark }}</span>
            </span>
            <span v-if="account.id === props.currentId" class="option-tag">当前</span>
          </button>
        </li>
      </ul>

      <div class="actions">
        <button type="button" class="submit" :disabled="pendingId === null" @click="confirm">
          确定
        </button>
        <button v-if="!props.required" type="button" class="ghost" @click="emit('close')">
          取消
        </button>
        <button v-else type="button" class="ghost" @click="emit('logout')">退出登录</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
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

.panel {
  width: 100%;
  max-width: 26rem;
  max-height: 80vh;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1.5rem 1.5rem 1.25rem;
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

.options {
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  /* 账套很多时面板自身滚动，不撑破弹窗 */
  overflow-y: auto;
}

.option {
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: none;
  color: inherit;
  font-size: 13.5px;
  font-family: inherit;
  text-align: left;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s,
    color 0.3s;
}

@media (hover: hover) {
  .option:hover {
    background-color: var(--color-accent-soft);
  }
}

.option.active {
  border-color: var(--color-accent);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
  font-weight: 600;
}

.option-main {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  min-width: 0;
}

.option-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.option-remark {
  font-size: 12px;
  font-weight: 400;
  opacity: 0.7;
  line-height: 1.5;
}

.option-tag {
  flex: none;
  padding: 0.1rem 0.45rem;
  border: 1px solid var(--color-accent);
  border-radius: 999px;
  font-size: 11px;
  font-weight: 400;
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 0.25rem;
}

/* 实心主色按钮：弹窗内的唯一强视觉锚点（与登录页提交按钮同源） */
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

@media (hover: hover) {
  .submit:not(:disabled):hover {
    border-color: var(--color-accent-strong);
    background: var(--color-accent-strong);
  }
}

.submit:disabled {
  opacity: 0.55;
  cursor: not-allowed;
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
  .ghost:hover {
    border-color: var(--color-accent);
    background-color: var(--color-accent-soft);
    color: var(--color-accent-strong);
  }
}
</style>
