<script setup lang="ts">
/**
 * /openapi 页面：读取后端 OpenAPI 文档并提供接口调试能力。
 *
 * 文档地址来自运行时配置 `public/conf/setting.json`，仅在开发环境由后端暴露。
 */
import { computed, onMounted, ref } from 'vue'
import OperationDetail from '@/components/openapi/OperationDetail.vue'
import OperationList from '@/components/openapi/OperationList.vue'
import { fetchOpenApiDocument, getOpenApiDocUrl, toOperationEntries } from '@/api/openapi/client'
import type { OpenApiDocument, OperationEntry } from '@/api/openapi/types'

type Phase = 'loading' | 'ready' | 'error'

const phase = ref<Phase>('loading')
const errorMessage = ref('')
const doc = ref<OpenApiDocument | null>(null)
const selected = ref<OperationEntry | null>(null)

const docUrl = getOpenApiDocUrl()
const entries = computed(() => toOperationEntries(doc.value))

/** 载入文档并默认选中第一个接口。 */
async function load() {
  phase.value = 'loading'
  errorMessage.value = ''

  try {
    doc.value = await fetchOpenApiDocument()
    selected.value = entries.value[0] ?? null
    phase.value = 'ready'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
    phase.value = 'error'
  }
}

onMounted(load)
</script>

<template>
  <main class="openapi">
    <header class="toolbar">
      <div>
        <h1 class="title">接口调试</h1>
        <p class="doc-url">
          文档地址：<code>{{ docUrl }}</code>
          <template v-if="doc?.info?.title"> · {{ doc.info.title }} {{ doc.info.version }}</template>
        </p>
      </div>
      <button type="button" class="reload" :disabled="phase === 'loading'" @click="load">
        {{ phase === 'loading' ? '载入中…' : '重新载入文档' }}
      </button>
    </header>

    <p v-if="phase === 'loading'" class="state">正在载入接口文档…</p>

    <div v-else-if="phase === 'error'" class="state state-error">
      <strong>无法载入接口文档</strong>
      <span>{{ errorMessage }}</span>
      <span class="state-hint">
        OpenAPI 文档仅在开发环境暴露（<code>/openapi/v1.json</code>）。请确认后端以 Development
        环境启动，并检查 <code>public/conf/setting.json</code> 中的 <code>api.baseUrl</code> 是否指向正确的后端地址。
      </span>
    </div>

    <p v-else-if="entries.length === 0" class="state">文档已载入，但没有可用接口。</p>

    <div v-else class="panes">
      <OperationList :entries="entries" :selected-key="selected?.key ?? ''" @select="selected = $event" />
      <OperationDetail v-if="selected" :doc="doc" :entry="selected" />
    </div>
  </main>
</template>

<style scoped>
/* 首页/关于页在宽屏下是两列网格（见 main.css），此处横跨整宽以容纳调试面板 */
.openapi {
  grid-column: 1 / -1;
  display: flex;
  flex-direction: column;
  gap: 1rem;
  width: 100%;
  margin-top: 2rem;
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
}

.title {
  font-size: 20px;
  font-weight: 600;
  color: var(--color-heading);
}

.doc-url {
  margin-top: 0.25rem;
  font-size: 12.5px;
  opacity: 0.7;
  word-break: break-all;
}

.reload {
  padding: 0.5rem 1rem;
  border: 1px solid var(--color-border-hover);
  border-radius: 6px;
  background: none;
  color: inherit;
  font-size: 13px;
  cursor: pointer;
}

.reload:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.state {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: var(--color-background-soft);
  font-size: 13.5px;
}

.state-error {
  border-color: rgba(220, 38, 38, 0.4);
  background: rgba(220, 38, 38, 0.08);
}

.state-hint {
  opacity: 0.75;
  font-size: 12.5px;
  line-height: 1.7;
}

.panes {
  display: grid;
  grid-template-columns: minmax(16rem, 22rem) 1fr;
  gap: 1rem;
  height: 78vh;
  min-height: 32rem;
}

@media (max-width: 1023px) {
  .panes {
    grid-template-columns: 1fr;
    height: auto;
  }
}
</style>
