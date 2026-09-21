/**
 * OpenAPI 3.x 文档的最小必要类型声明。
 *
 * 仅覆盖调试面板渲染与发请求所需的字段，不追求规范全覆盖；未使用的字段一律忽略。
 */

/** `$ref` 引用节点，例如 `{ $ref: '#/components/schemas/Foo' }`。 */
export interface OpenApiReference {
  $ref: string
}

/** Schema 节点（含 `$ref` 形式）。 */
export interface OpenApiSchema {
  $ref?: string
  /** OpenAPI 3.1 允许类型为数组（如 `["null", "string"]`），故此处两种形态都要兼容。 */
  type?: string | string[]
  format?: string
  title?: string
  description?: string
  default?: unknown
  example?: unknown
  enum?: unknown[]
  nullable?: boolean
  properties?: Record<string, OpenApiSchema>
  required?: string[]
  items?: OpenApiSchema
  additionalProperties?: boolean | OpenApiSchema
  allOf?: OpenApiSchema[]
  oneOf?: OpenApiSchema[]
  anyOf?: OpenApiSchema[]
}

/** 参数位置。 */
export type OpenApiParameterLocation = 'path' | 'query' | 'header' | 'cookie'

/** 单个接口参数。 */
export interface OpenApiParameter {
  name: string
  in: OpenApiParameterLocation
  description?: string
  required?: boolean
  deprecated?: boolean
  schema?: OpenApiSchema
  example?: unknown
}

/** 请求体媒体类型定义。 */
export interface OpenApiMediaType {
  schema?: OpenApiSchema
  example?: unknown
}

/** 请求体。 */
export interface OpenApiRequestBody {
  description?: string
  required?: boolean
  content?: Record<string, OpenApiMediaType>
}

/** 响应定义。 */
export interface OpenApiResponse {
  description?: string
  content?: Record<string, OpenApiMediaType>
}

/** 单个接口操作。 */
export interface OpenApiOperation {
  tags?: string[]
  summary?: string
  description?: string
  operationId?: string
  deprecated?: boolean
  parameters?: OpenApiParameter[]
  requestBody?: OpenApiRequestBody
  responses?: Record<string, OpenApiResponse>
}

/** 路径项：HTTP 方法映射到操作。 */
export type OpenApiPathItem = Partial<Record<HttpMethod, OpenApiOperation>> & {
  summary?: string
  description?: string
}

/** 支持的 HTTP 方法（OpenAPI 路径项中可作为键的方法）。 */
export type HttpMethod = 'get' | 'put' | 'post' | 'delete' | 'options' | 'head' | 'patch' | 'trace'

/** 规范中用于描述路径项的方法键集合，用于过滤 `parameters` / `summary` 等非方法键。 */
export const HTTP_METHODS: readonly HttpMethod[] = [
  'get',
  'put',
  'post',
  'delete',
  'options',
  'head',
  'patch',
  'trace',
]

/** 服务器地址声明。 */
export interface OpenApiServer {
  url: string
  description?: string
}

/** 顶层文档结构。 */
export interface OpenApiDocument {
  openapi?: string
  info?: {
    title?: string
    version?: string
    description?: string
  }
  servers?: OpenApiServer[]
  paths?: Record<string, OpenApiPathItem>
  tags?: { name: string; description?: string }[]
  components?: {
    schemas?: Record<string, OpenApiSchema>
  }
}

/** 清单/详情共用的规范化接口条目。 */
export interface OperationEntry {
  /** 唯一键，形如 `get /health`。 */
  key: string
  method: HttpMethod
  path: string
  operation: OpenApiOperation
  /** 用于分组与展示的标签，取 `tags[0]`，缺省为「默认分组」。 */
  tag: string
}

/** 一次调试请求的结果。 */
export interface InvokeResult {
  status: number
  statusText: string
  durationMs: number
  /** 最终请求地址（已替换路径参数并拼装查询串）。 */
  url: string
  headers: [string, string][]
  /** 原始响应文本。 */
  bodyText: string
  /** 响应体可解析为 JSON 时的解析结果，否则为 `null`。 */
  bodyJson: unknown
}
