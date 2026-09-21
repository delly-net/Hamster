/**
 * Schema 工具：`$ref` 解引用、类型标签生成、示例值推导。
 *
 * `MapOpenApi` 生成的请求/响应体会以 `#/components/schemas/Xxx` 形式引用，
 * 必须解引用后才能渲染字段与生成可直接发送的示例 JSON。
 */

import type { OpenApiDocument, OpenApiSchema } from './types'

/** 示例值递归生成的最大深度，防止深层嵌套结构把界面撑爆。 */
const MAX_SAMPLE_DEPTH = 5

/** 从 `#/components/schemas/Foo` 形式的引用中取出名称。 */
function refName(ref: string): string {
  const segments = ref.split('/')
  return segments[segments.length - 1] ?? ref
}

/**
 * 解引用 schema。仅支持 `#/components/schemas/` 下的本地引用。
 *
 * @param doc 顶层文档，用于查表。
 * @param schema 待解引用的 schema。
 * @param depth 当前递归深度，超过 {@link MAX_SAMPLE_DEPTH} 时不再展开。
 * @returns 解引用后的 schema；无法解析时原样返回。
 */
export function resolveSchema(
  doc: OpenApiDocument | null,
  schema: OpenApiSchema | undefined,
  depth = 0,
): OpenApiSchema | undefined {
  if (!schema || depth > MAX_SAMPLE_DEPTH) {
    return schema
  }

  if (!schema.$ref) {
    return schema
  }

  const name = refName(schema.$ref)
  const target = doc?.components?.schemas?.[name]
  return target ? { ...target, title: target.title ?? name } : { $ref: schema.$ref, title: name }
}

/**
 * 取 schema 的主类型。
 *
 * OpenAPI 3.1 会把可空类型写成 `["null", "string"]` 这样的数组，也可能写成
 * `["number", "string"]` 的联合。此处忽略 `null` 并取第一个具体类型作为主类型，
 * 用于推导示例值与展示类型标签。
 */
export function primaryType(schema?: OpenApiSchema): string | undefined {
  const { type } = schema ?? {}
  if (!type) {
    return undefined
  }

  if (typeof type === 'string') {
    return type
  }

  return type.find((item) => item !== 'null') ?? type[0]
}

/**
 * 生成人类可读的类型标签，用于参数表与请求体说明。
 *
 * @example `string(date-time)` / `array<string>` / `Account`
 */
export function schemaTypeLabel(doc: OpenApiDocument | null, schema?: OpenApiSchema): string {
  if (!schema) {
    return 'any'
  }

  if (schema.$ref) {
    return refName(schema.$ref)
  }

  if (schema.enum?.length) {
    return `enum(${schema.enum.map((item) => String(item)).join(' | ')})`
  }

  const type = primaryType(schema)

  if (type === 'array') {
    return `array<${schemaTypeLabel(doc, resolveSchema(doc, schema.items))}>`
  }

  if (type) {
    return schema.format ? `${type}(${schema.format})` : type
  }

  if (schema.properties) {
    return 'object'
  }

  return 'any'
}

/** 按 `format` 生成一个占位字符串，让示例值更贴近真实数据形态。 */
function sampleString(format?: string): string {
  switch (format) {
    case 'date-time':
      return new Date().toISOString()
    case 'date':
      return new Date().toISOString().slice(0, 10)
    case 'uuid':
      return '00000000-0000-0000-0000-000000000000'
    case 'email':
      return 'user@example.com'
    case 'uri':
      return 'https://example.com'
    default:
      return ''
  }
}

/**
 * 依据 schema 推导示例值，用于预填请求体编辑框。
 *
 * 优先使用 schema 自带的 `example` / `default`，否则按类型推导；
 * 对象展开属性、数组给一个元素样例，并在深度超限时返回 `null`。
 */
export function buildSampleValue(
  doc: OpenApiDocument | null,
  schema?: OpenApiSchema,
  depth = 0,
): unknown {
  const resolved = resolveSchema(doc, schema, depth)
  if (!resolved || depth > MAX_SAMPLE_DEPTH) {
    return null
  }

  if (resolved.example !== undefined) {
    return resolved.example
  }

  if (resolved.default !== undefined) {
    return resolved.default
  }

  if (resolved.enum?.length) {
    return resolved.enum[0]
  }

  // 组合类型取第一个分支作为代表
  const branch = resolved.allOf?.[0] ?? resolved.oneOf?.[0] ?? resolved.anyOf?.[0]
  if (branch) {
    return buildSampleValue(doc, branch, depth + 1)
  }

  // 有 properties 时按对象处理，兼容未显式声明 type 的 object
  if (resolved.properties) {
    return Object.fromEntries(
      Object.entries(resolved.properties).map(([key, value]) => [
        key,
        buildSampleValue(doc, value, depth + 1),
      ]),
    )
  }

  switch (primaryType(resolved)) {
    case 'object':
      return {}
    case 'array':
      return [buildSampleValue(doc, resolved.items, depth + 1)]
    case 'integer':
    case 'number':
      return 0
    case 'boolean':
      return false
    case 'string':
      return sampleString(resolved.format)
    default:
      return null
  }
}

/** 把示例值序列化成适合放进编辑框的 JSON 文本。 */
export function toJsonText(value: unknown): string {
  if (value === undefined || value === null) {
    return ''
  }

  return JSON.stringify(value, null, 2)
}
