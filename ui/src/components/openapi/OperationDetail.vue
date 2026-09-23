<script setup lang="ts">
/** 接口详情与调试面板：填参 → 发送真实请求 → 查看响应。 */
import { computed, ref, watch } from 'vue'
import { invokeOperation } from '@/api/openapi/client'
import { buildSampleValue, resolveSchema, schemaTypeLabel, toJsonText } from '@/api/openapi/schema'
import type {
  InvokeResult,
  OpenApiDocument,
  OpenApiParameter,
  OperationEntry,
} from '@/api/openapi/types'

const props = defineProps<{
  doc: OpenApiDocument | null
  entry: OperationEntry
}>()

/** 各参数位置的取值表，键为参数名。 */
const pathValues = ref<Record<string, string>>({})
const queryValues = ref<Record<string, string>>({})
const headerValues = ref<Record<string, string>>({})

const bodyText = ref('')
const sending = ref(false)
const errorMessage = ref('')
const result = ref<InvokeResult | null>(null)
const showResponseHeaders = ref(false)
const copied = ref(false)

/** 按 `in` 分组的参数列表，空组不渲染。 */
function paramsIn(location: OpenApiParameter['in']): OpenApiParameter[] {
  return (props.entry.operation.parameters ?? []).filter((item) => item.in === location)
}

const pathParams = computed(() => paramsIn('path'))
const queryParams = computed(() => paramsIn('query'))
const headerParams = computed(() => paramsIn('header'))

/** 请求体的 JSON 媒体类型；无 JSON 请求体时为 `null`。 */
const jsonContentType = computed(() => {
  const content = props.entry.operation.requestBody?.content
  if (!content) {
    return null
  }

  return 'application/json' in content ? 'application/json' : null
})

/** 非 JSON 请求体的媒体类型，仅用于给出提示。 */
const otherContentType = computed(() => {
  const content = props.entry.operation.requestBody?.content
  if (!content || jsonContentType.value) {
    return null
  }

  return Object.keys(content)[0] ?? null
})

/** 依据 schema 推导的示例请求体文本。 */
const sampleBodyText = computed(() => {
  const contentType = jsonContentType.value
  if (!contentType) {
    return ''
  }

  const schema = resolveSchema(
    props.doc,
    props.entry.operation.requestBody?.content?.[contentType]?.schema,
  )
  return toJsonText(buildSampleValue(props.doc, schema))
})

/** 切换接口时重置全部表单与响应。 */
function resetForm() {
  const pick = (params: OpenApiParameter[]) =>
    Object.fromEntries(params.map((item) => [item.name, '']))

  pathValues.value = pick(pathParams.value)
  queryValues.value = pick(queryParams.value)
  headerValues.value = pick(headerParams.value)
  bodyText.value = sampleBodyText.value
  errorMessage.value = ''
  result.value = null
  showResponseHeaders.value = false
  copied.value = false
}

watch(() => props.entry.key, resetForm, { immediate: true })

/** 响应体的展示文本：优先 JSON 美化，否则原文。 */
const prettyBody = computed(() => {
  if (!result.value) {
    return ''
  }

  return result.value.bodyJson === null
    ? result.value.bodyText
    : JSON.stringify(result.value.bodyJson, null, 2)
})

/** 状态码对应的配色语义。 */
const statusClass = computed(() => {
  const status = result.value?.status ?? 0
  if (status >= 200 && status < 300) return 'status-ok'
  if (status >= 400 && status < 500) return 'status-warn'
  if (status >= 500) return 'status-error'
  return 'status-plain'
})

/** 路径参数全填齐才允许发送。 */
const canSend = computed(() =>
  pathParams.value
    .filter((item) => item.required)
    .every((item) => pathValues.value[item.name] !== ''),
)

async function send() {
  sending.value = true
  errorMessage.value = ''
  result.value = null
  copied.value = false

  try {
    result.value = await invokeOperation({
      method: props.entry.method,
      path: props.entry.path,
      pathValues: pathValues.value,
      queryValues: queryValues.value,
      headers: headerValues.value,
      body: bodyText.value,
    })
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    sending.value = false
  }
}

async function copyResponse() {
  try {
    await navigator.clipboard.writeText(prettyBody.value)
    copied.value = true
    setTimeout(() => (copied.value = false), 1500)
  } catch {
    errorMessage.value = '复制失败：当前浏览器不允许访问剪贴板。'
  }
}
</script>

<template>
  <section class="detail">
    <header class="head">
      <div class="head-line">
        <span class="method" :class="`method-${entry.method}`">{{
          entry.method.toUpperCase()
        }}</span>
        <code class="path">{{ entry.path }}</code>
      </div>
      <h2 v-if="entry.operation.summary" class="summary">{{ entry.operation.summary }}</h2>
      <p v-if="entry.operation.description" class="description">
        {{ entry.operation.description }}
      </p>
      <p v-if="entry.operation.operationId" class="operation-id">
        operationId：{{ entry.operation.operationId }}
      </p>
    </header>

    <!-- 路径参数 -->
    <section v-if="pathParams.length" class="block">
      <h3 class="block-title">路径参数</h3>
      <div v-for="param in pathParams" :key="param.name" class="param">
        <label :for="`path-${param.name}`" class="param-label">
          {{ param.name }}
          <span v-if="param.required" class="required">*</span>
          <span class="type">{{ schemaTypeLabel(doc, resolveSchema(doc, param.schema)) }}</span>
        </label>
        <input
          :id="`path-${param.name}`"
          v-model="pathValues[param.name]"
          type="text"
          :placeholder="param.description"
        />
      </div>
    </section>

    <!-- 查询参数 -->
    <section v-if="queryParams.length" class="block">
      <h3 class="block-title">查询参数</h3>
      <div v-for="param in queryParams" :key="param.name" class="param">
        <label :for="`query-${param.name}`" class="param-label">
          {{ param.name }}
          <span v-if="param.required" class="required">*</span>
          <span class="type">{{ schemaTypeLabel(doc, resolveSchema(doc, param.schema)) }}</span>
        </label>
        <input
          :id="`query-${param.name}`"
          v-model="queryValues[param.name]"
          type="text"
          :placeholder="param.description"
        />
      </div>
    </section>

    <!-- 请求头 -->
    <section v-if="headerParams.length" class="block">
      <h3 class="block-title">请求头</h3>
      <div v-for="param in headerParams" :key="param.name" class="param">
        <label :for="`header-${param.name}`" class="param-label">
          {{ param.name }}
          <span v-if="param.required" class="required">*</span>
          <span class="type">{{ schemaTypeLabel(doc, resolveSchema(doc, param.schema)) }}</span>
        </label>
        <input
          :id="`header-${param.name}`"
          v-model="headerValues[param.name]"
          type="text"
          :placeholder="param.description"
        />
      </div>
    </section>

    <!-- 请求体 -->
    <section v-if="jsonContentType" class="block">
      <h3 class="block-title">
        请求体
        <span class="type">{{ jsonContentType }}</span>
        <button type="button" class="text-button" @click="bodyText = sampleBodyText">
          重置为示例
        </button>
      </h3>
      <textarea v-model="bodyText" class="body-editor" rows="10" spellcheck="false"></textarea>
    </section>

    <p v-else-if="otherContentType" class="hint">
      该接口请求体为 {{ otherContentType }}，当前调试面板仅支持 application/json 请求体。
    </p>

    <div class="actions">
      <button type="button" class="send" :disabled="sending || !canSend" @click="send">
        {{ sending ? '请求中…' : '发送请求' }}
      </button>
      <span v-if="!canSend" class="hint">请先填写必填路径参数</span>
    </div>

    <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

    <!-- 响应结果 -->
    <section v-if="result" class="block response">
      <h3 class="block-title">响应</h3>

      <div class="response-meta">
        <span class="status" :class="statusClass">{{ result.status }} {{ result.statusText }}</span>
        <span class="duration">{{ result.durationMs }} ms</span>
        <code class="url">{{ result.url }}</code>
      </div>

      <button type="button" class="text-button" @click="showResponseHeaders = !showResponseHeaders">
        {{ showResponseHeaders ? '收起响应头' : `响应头（${result.headers.length}）` }}
      </button>

      <dl v-if="showResponseHeaders" class="headers">
        <template v-for="[name, value] in result.headers" :key="name">
          <dt>{{ name }}</dt>
          <dd>{{ value }}</dd>
        </template>
      </dl>

      <div class="body-toolbar">
        <span class="block-title">响应体</span>
        <button type="button" class="text-button" @click="copyResponse">
          {{ copied ? '已复制' : '复制' }}
        </button>
      </div>
      <pre class="response-body">{{ prettyBody || '（空响应体）' }}</pre>
    </section>
  </section>
</template>

<style scoped>
.detail {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
  overflow-y: auto;
  padding-left: 1rem;
}

.head-line {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.path {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 14px;
  word-break: break-all;
}

.summary {
  margin-top: 0.5rem;
  font-size: 16px;
  font-weight: 600;
  color: var(--color-heading);
}

.description,
.operation-id {
  margin-top: 0.375rem;
  font-size: 13px;
  opacity: 0.7;
}

.block {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.block-title {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.04em;
  opacity: 0.65;
}

.param {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.param-label {
  display: flex;
  align-items: center;
  gap: 0.375rem;
  font-size: 13px;
}

.required {
  color: var(--color-danger);
}

.type {
  font-size: 11.5px;
  opacity: 0.6;
  font-weight: 400;
}

input,
.body-editor {
  width: 100%;
  padding: 0.5rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: var(--color-background);
  color: var(--color-text);
  font-size: 13px;
}

.body-editor {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  resize: vertical;
}

input:focus,
.body-editor:focus {
  outline: 2px solid var(--color-accent);
  outline-offset: 1px;
}

.text-button {
  border: none;
  background: none;
  color: var(--color-accent-strong);
  font-size: 12.5px;
  cursor: pointer;
  padding: 0;
}

.text-button:hover {
  text-decoration: underline;
}

.actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.send {
  padding: 0.5rem 1.25rem;
  border: none;
  border-radius: var(--radius-control);
  background: var(--color-accent);
  color: var(--color-accent-contrast);
  font-size: 13.5px;
  font-weight: 600;
  cursor: pointer;
}

.send:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.hint {
  font-size: 12.5px;
  opacity: 0.65;
}

.error {
  padding: 0.625rem 0.875rem;
  border: 1px solid var(--color-danger-border);
  border-radius: var(--radius-control);
  background: var(--color-danger-soft);
  color: var(--color-danger);
  font-size: 13px;
}

.response-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem;
  font-size: 13px;
}

.status {
  padding: 2px 8px;
  border-radius: 4px;
  font-weight: 600;
  color: #fff;
}

.status-ok {
  background: #16a34a;
}

.status-warn {
  background: #d97706;
}

.status-error {
  background: #dc2626;
}

.status-plain {
  background: #64748b;
}

.duration {
  opacity: 0.65;
}

.url {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  opacity: 0.65;
  word-break: break-all;
}

.headers {
  display: grid;
  grid-template-columns: minmax(8rem, auto) 1fr;
  gap: 0.25rem 0.75rem;
  font-size: 12px;
}

.headers dt {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  opacity: 0.65;
}

.headers dd {
  word-break: break-all;
}

.body-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.response-body {
  max-height: 26rem;
  overflow: auto;
  padding: 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: var(--color-background-soft);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12.5px;
  white-space: pre-wrap;
  word-break: break-all;
}

.method {
  padding: 1px 6px;
  border-radius: 4px;
  font-size: 11px;
  font-weight: 700;
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

@media (max-width: 1023px) {
  .detail {
    padding-left: 0;
  }
}
</style>
