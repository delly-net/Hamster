<script setup lang="ts">
/** 接口清单：按 Tag 分组展示，支持关键字过滤。 */
import { computed, ref } from 'vue'
import type { OperationEntry } from '@/api/openapi/types'

const props = defineProps<{
  entries: OperationEntry[]
  selectedKey: string
}>()

const emit = defineEmits<{
  select: [entry: OperationEntry]
}>()

const keyword = ref('')

const filtered = computed(() => {
  const term = keyword.value.trim().toLowerCase()
  if (!term) {
    return props.entries
  }

  return props.entries.filter((entry) =>
    [entry.path, entry.method, entry.operation.summary, entry.operation.operationId]
      .filter(Boolean)
      .some((field) => String(field).toLowerCase().includes(term)),
  )
})

/** 按 Tag 分组，保持文档中的原始顺序。 */
const groups = computed(() => {
  const map = new Map<string, OperationEntry[]>()
  for (const entry of filtered.value) {
    const bucket = map.get(entry.tag)
    if (bucket) {
      bucket.push(entry)
    } else {
      map.set(entry.tag, [entry])
    }
  }
  return [...map.entries()].map(([tag, items]) => ({ tag, items }))
})
</script>

<template>
  <aside class="operation-list">
    <input v-model="keyword" class="search" type="search" placeholder="搜索路径 / 摘要 / operationId" />

    <p v-if="groups.length === 0" class="empty">没有匹配的接口</p>

    <section v-for="group in groups" :key="group.tag" class="group">
      <h3 class="group-title">{{ group.tag }}</h3>

      <button
        v-for="entry in group.items"
        :key="entry.key"
        type="button"
        class="item"
        :class="{ active: entry.key === selectedKey }"
        @click="emit('select', entry)"
      >
        <span class="method" :class="`method-${entry.method}`">{{ entry.method.toUpperCase() }}</span>
        <span class="item-body">
          <span class="path">{{ entry.path }}</span>
          <span v-if="entry.operation.summary" class="summary">{{ entry.operation.summary }}</span>
        </span>
      </button>
    </section>
  </aside>
</template>

<style scoped>
.operation-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  overflow-y: auto;
  padding-right: 0.5rem;
}

.search {
  padding: 0.5rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: var(--color-text);
  font-size: 13px;
}

.search:focus {
  outline: 2px solid var(--color-accent);
  outline-offset: 1px;
}

.empty {
  color: var(--color-text);
  opacity: 0.6;
  font-size: 13px;
  padding: 0.5rem;
}

.group-title {
  margin-bottom: 0.375rem;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.04em;
  color: var(--color-text);
  opacity: 0.65;
}

.item {
  display: flex;
  gap: 0.5rem;
  align-items: flex-start;
  width: 100%;
  padding: 0.5rem;
  border: 1px solid transparent;
  border-radius: var(--radius-control);
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
  transition:
    background-color 0.2s,
    border-color 0.2s;
}

.item:hover {
  background: var(--color-background-mute);
}

.item.active {
  border-color: var(--color-accent);
  background: var(--color-accent-soft);
}

.item-body {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.path {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12.5px;
  word-break: break-all;
}

.summary {
  font-size: 12px;
  opacity: 0.65;
}

.method {
  flex-shrink: 0;
  min-width: 3.5rem;
  padding: 1px 4px;
  border-radius: 4px;
  font-size: 10.5px;
  font-weight: 700;
  text-align: center;
  color: #fff;
}

.method-get {
  background: #2563eb;
}

.method-post {
  background: #16a34a;
}

.method-put {
  background: #d97706;
}

.method-patch {
  background: #7c3aed;
}

.method-delete {
  background: #dc2626;
}

.method-head,
.method-options,
.method-trace {
  background: #64748b;
}
</style>
